# StreamTransport 1.0 restructure ledger

Working ledger for ADR-118 (StreamWeaver) and issues #20, #22, #26. Committed for handoff to a cloud session (2026-10-07). Specifications cited
here are in `docs/references/` (RFCs as published; drafts at their current revision, fetched
2026-10-06).
Status: `done`, `active`, `todo`, `deferred`, `needs decision`. Evidence marks: [V] verified on hardware
or by test, [S] read in source or spec, [A] assumed.

**Standing goal: write in place everywhere (user, 2026-10-07).** Every conversion writes its result directly
into the surface that is delivered (encoder input, sink buffer, shared texture), with no intermediate
surface and no second pass, on every platform and API path we support; a copy is only the fallback where
the device cannot do it, chosen by a capability probe, never the default. Status of the GPU processors:
- D3D12: in place since A6 (plane-slice UAVs on NV12 where the device stores to it; copy fallback).
- Vulkan: in place: the output DMA-BUF is built from the R8 and R8G8 plane images the shader writes.
  Open: per-plane imports of tiled NV12 (Z19); the copy fallback rebuilt per frame (Z9).
- Metal: in place: the kernel writes the IOSurface's plane textures.
- Still copying, to fix toward this goal: Windows 1080p transcode 1088/1152 alignment (Z10), decoded
  frames into PipeWire sink buffers (Z23), nothing else on the FlexFEC path (F23 done).

## Where we are (2026-10-07)

Steps 1 to 6 are done (transport correctness, the ADR-119 split, SCReAMv2, ECN, path MTU with Dtls.Core
connection IDs), and step 7a, media mobility (T25 to T29), is done: failover on silence in seconds,
network events without interruption, continual gathering, congestion restart on new addresses, WHIP/WHEP
trickle and ICE restart. Step 7 has started: FlexFEC is negotiated in SDP (T14 part); the joint recovery
policy (T15) is designed, not built.

Remaining, by step (units, about half a day each with three-machine verification): 7 recovery policy and
FlexFEC masks 4; 7b path-diverse repair over the second modem (T32) 3; alpha and colour corrections A2-A10
5; codec roadmap research C1 (VVC, AV1 complete, AV2 prep) 2; 8 network-emulation rig 6; 9 zero-copy and
other hardware 8; 10 ARM codec engines 8; 11 follow-ups 5; 12 end review and ADRs 3; 13 Casting move,
releases, merge 3. About 47 units.

StreamWeaver can resume against a stable public surface after G1 (steps 1 and 3 are done); the
field agent additionally needs step 10. Steps 4-9 change behaviour behind that surface.

QUIC, QUIC DATAGRAM and MoQ stay evaluation only (T21); an implementation is its own ADR.

## Plan, in order

Standing rules for every step (user): zero-copy on every modern platform through the native stacks
(D3D12, Vulkan with DMA-BUF, Metal/IOSurface/VideoToolbox), no CPU waits on the GPU (sync points), no
unnecessary copies, conversions or waits, hardware blocks where present, minimal CPU and GPU load
because streamers game while they stream; modern, idiomatic, extensible first-party-style .NET;
greenfield, so restructure where it is better. The zero-copy findings and their status are in the
zero-copy table (Z rows) under History; the open ones are step 9.

Each step builds on the ones before it. Items reference the tables below.

1. **Correctness inside today's layering** (no design decision needed; the fixes carry over):
   ~~RTCP reporting (T22) and the CCFB timestamp on its NTP clock (T7)~~ done;
   **done** (8b19d76 .. 19bc4d4): RTCP reports (T22, T7), RTX and FEC in the budget (T4, T5), reordering
   window (T6), CCFB rules (T8), ccfb negotiation (T23), circuit breakers (T9).
2. ~~ADR-119: transport-independent adaptation layer~~ **accepted 2026-10-06** (StreamWeaver
   `581c30a4`). Step 1's remaining fixes land directly in its layout: the pacer moves into
   `Agash.StreamTransport.Adaptation` first, with RTX and FlexFEC inside it.
3. **The move** (**done**, ac0b17c .. f26328e; verified on the three machines when the suites report): neutral feedback and controller contracts, pacing and media-rate allocation in
   Adaptation (**done** with step 1), then the frame-level split (design 2026-10-06):
   - `Media` gains the neutral pieces both sides share: `EncodedFrameBuffer` (pooled, owned; from WebRtc)
     and a public `NtpTime` (capture wall-clock; from core Sync).
   - `Abstractions` gains the transport contract (refs Media, Adaptation): `IMediaTransport`
     (state, connect with a `MediaOffer` returning `NegotiatedMedia`, capacity, sent bytes per class,
     keyframe requests, `TrySendVideo`/`TrySendAudio` of encoded frames with their capture time, route,
     statistics, circuit breaker) and `IMediaTransportFactory`; received frames arrive owned through
     `IReceivedMediaConsumer` (video: frame, keyframe, capture; audio: frame, sequence, stream position,
     capture), so no frame is copied on its way to the decoder.
   - `WebRtc` becomes the adapter (refs Media, Abstractions, Adaptation): `WebRtcMediaTransport` owns SDP
     codec offers and answers (alpha fmtp, H.264 profile defaults), RTP packetization (`RtpStreamWriter`),
     frame assembly and RTP-to-capture clock alignment (`RtpClockAligner`), NACK gap patience and keyframe
     request throttling, STUN/TURN resolution and network-change recovery.
   - Core composition (`MediaSession`) is transport-agnostic: sources, encoders, decoders, playout and
     sync, the rate allocator, rooms; it references no WebRtc package. `AddStreamTransport()` plus
     `AddStreamTransportWebRtc()` from the WebRtc DI package registers the transport.
   - `IMediaSession` exposes `TransportState` and neutral `TransportStatistics`, and surfaces the circuit
     breaker (F13).
4. **SCReAMv2** (**done**, e0abd8b) on the new contracts (T10): reference window, once-per-RTT reductions, classic ECN vs
   L4S CE vs loss vs delay (pseudo-L4S), bytes in flight, minimum rate, source-limited, ref-window
   validation; line by line against `draft-ietf-ccwg-rfc8298bis-screamv2-01`.
5. **ECN as a path property** (**done**, 8b70516) (T11, T24): validation, blackhole and bleaching detection, fallback, re-probe;
   classic-ECN response distinct from L4S.
6. **Path MTU** (T12, T13): packetization from a path MTU instead of the fixed 1100-byte payload; DPLPMTUD
   (RFC 8899) probing with padded DTLS 1.3 records; DTLS connection IDs (RFC 9146/9147) in Dtls.Core so
   a NAT rebinding does not need an ICE restart.
7. **Recovery policy** (T14, T15): FlexFEC against RFC 8627 vs libwebrtc's flexfec-03 (interop target
   decides the format), interleaved masks, adaptive repair rate; FEC vs RTX chosen by RTT, playout
   deadline and budget.
8. **Network-emulation rig and measurements** (T16): bandwidth steps, loss, bursts, reordering, RTT spikes,
   ECN/L4S marking, blackholes, feedback loss, MTU changes; metrics per the brief's section 29. Run on the
   lab box with netem (and an L4S DualQ AQM where available).
9. **Remaining zero-copy** (Z9, Z10, Z19, Z23, Z30) and verification on other hardware (Z17, Z18, Z20).
10. **ARM codec engines** (Z26): `Rkmpp` backend (DRM PRIME, after Z29), own V4L2 M2M backend with DMA-BUF
    queues; verify on the Qualcomm board. Prerequisite for the field agent.
11. **Follow-ups** (F1-F10 below).
12. **End-of-restructure review** (pattern review, timing sweep, ADR-117 sweep), ADR updates (ADR-108,
    ADR-118, ADR-119), GH issues, then a second independent read-only review.
13. **G1** Casting move into StreamWeaver; **H1** releases as -alpha (user's call); **I1** merge
    `restructure` into `main`.

Also fixed with T4/T5 (7b4ce55): SRTP protection ran concurrently between media sends and NACK
repairs (now one send loop); the receiver NACKed packets FlexFEC had already recovered; the SDP's
`a=ssrc ... cname:` still carried the fixed name while RTCP sent the per-connection one;
`RtpStreamWriter` copied each payload twice on its way to the socket (now once, into the packet).
`PeerConnection.SendRtp` is now `TrySendRtp`, dropping before DTLS-SRTP is up.

## Transport, congestion and recovery audit (2026-10-06)

From an external brief, every item checked against the code at `f2e589a` and the specification text.

| # | Finding | Status |
|---|---|---|
| T1 | The congestion contract is RTP-shaped: `INetworkController` lives in `WebRtc.Abstractions`, `SentPacketInfo`/`PacketResult` key packets by a 16-bit RTP sequence number, the package is `WebRtc.CongestionControl` [S] Controller contract, SCReAM, `DeliveryTracker`, `CapacityEstimate` now in Adaptation with opaque packet ids; `WebRtc.CongestionControl` deleted. Remaining: composition free of WebRtc (T2). | done (fbdf464, 3bc411c) |
| T2 | Composition is WebRTC-bound: `Agash.StreamTransport` references `.WebRtc` and `.WebRtc.DependencyInjection`, owns `RtpPacer`/`PacingQueue`/`RtpStreamWriter`, and the send/receive streams use WebRtc types; `IMediaSessionFactory` has one implementation. Media, Codecs and the platform packages reference nothing WebRTC (ADR-108's "capture and codec untouched" holds) [S] Now: composition references no WebRtc package; `MediaSession` runs over `IMediaTransport`; `WebRtcMediaTransport` (WebRtc) owns SDP, packetization, frame assembly, clock alignment, STUN/TURN and mobility; WHIP/WHEP in AspNetCore keep their WebRtc reference by nature. | done (ac0b17c .. f26328e) |
| T3 | Transport congestion control vs media-rate adaptation are one thing today (the controller's target goes straight to the encoder). A QUIC transport owns its own controller; the media side must be able to consume a capacity estimate instead of driving packets [S] Now: the transport reports capacity and sent bytes per class; `MediaRateAllocator` in composition derives the encoder target, so a transport with its own controller plugs in by reporting capacity. | done (fbdf464, f6f8041) |
| T4 | RTX is sent straight to the socket (`PeerConnection.cs` NACK serving): not paced, not recorded for feedback, outside the budget. RFC 4588 section 7: "the acceptable packet rate and bitrate include both the original and retransmitted data" [S] Now: RTX queues at the pacer as the retransmission class, ahead of video, inside the budget; the receiver records the RTX packet's arrival under the RTX SSRC for CCFB and reception reports. | done (7b4ce55) |
| T5 | FlexFEC repairs are sent inline after their group through `SendRtp` (recorded, but skipping the pacer), and the encoder is still given the whole target rate, so repair traffic rides on top of the budget [S] Now: FlexFEC repairs queue as the lowest class; `MediaRateAllocator` (Adaptation) gives the encoder the target less audio and measured recovery traffic. | done (7b4ce55) |
| T6 | `LossEstimator.Update(..., numRecovered: 0, ...)`: losses later recovered by RTX or FlexFEC still count as congestion; no distinction of unrecovered, RTX-recovered, FEC-recovered, too late [S] Corrected premise: a drop is congestion whether or not RTX or FEC repaired it (SCReAMv2 section 4.1.2 counts only reordering as not-loss). Now: `DeliveryTracker` (Adaptation) declares loss after a learned reordering window and reports late arrivals as `DeliveredLate`, which the loss filter takes as recovered. Repair outcomes for the recovery policy are step 7. | done (fbdf464) |
| T7 | CCFB Report Timestamp is connection-relative (`now * 65536 / 1e6`); RFC 8888 section 3.1: "derived from the same clock used to generate the NTP timestamp field in RTCP Sender Reports ... the middle 32 bits of an NTP format timestamp" [S] | done (8b19d76) |
| T8 | CCFB receiver and sender against RFC 8888 section 3.1 [S, `PeerConnection.Congestion.cs`]: (a) each report covers only arrivals since the previous report, from the earliest arrival, so a packet lost before the first arrival of a window is never reported and reordering across a boundary is never corrected (the MUST that a packet reported received stays received in later overlapping reports cannot be met); (b) an over-range arrival offset is sent as 0x1FFF, the spec requires 0x1FFE; (c) runs are cut at 256 and the remainder dropped instead of split across packets; (d) the sender turns "received, arrival unknown" (0x1FFF) into lost; (e) each report is applied on its own, so a later report correcting an earlier one counts a packet both lost and received; (f) the 32-bit RTS wraps (about 18 h) and is not unwrapped; (g) RTT is taken as now minus the newest acked send time, which includes the receiver's hold time up to the 50 ms report interval, where now minus send minus arrival offset is available; (h) FEC and RTX SSRC coverage and the 50 ms fixed interval (no RTT or frame-rate scaling) unverified against interop peers Receiver: per-SSRC `CcfbReceiveTracker` after libwebrtc's (`docs/references/libwebrtc-congestion_control_feedback_tracker.cc`): overlap from a late packet, first-copy arrival time and sticky CE for duplicates (a rule the old code broke), 0x1FFE, blocks split at 512 and packets at 1200 bytes, frame-end timing at 25-250 ms under 500 kbit/s; RTX and FEC SSRCs covered since arrivals are recorded on the wire packet. Sender: received-without-time kept, each packet resolved once, RTS unwrapped, RTT net of hold time. | done (a53614e, fbdf464) |
| T9 | Feedback starvation only decays the window 5 % per process interval after 1 s; no circuit breaker (RFC 8083) distinguishing a lost report, intermittent loss, starvation and a vanished peer [S] Now: `CircuitBreaker` (Adaptation) per sent SSRC with RFC 8083's three breakers (RTCP timeout 3 x Td = 15 s, media timeout k = 5, congestion > 10 x TCP over CB_INTERVAL with the simplified equation by default and the full one as an option for LTE), first a cut to a tenth, then cease; `PeerConnection.CircuitBreakerChanged`, `TryResumeTransmission` with section 4.5's restart limit. One missed report does nothing and intermittent loss stays the controller's; ICE consent covers a vanished peer. | done (19bc4d4) |
| T10 | The controller is RFC 8298-style with two SCReAMv2 pieces (loss filter, L4S alpha): it backs off on every feedback while congested (the draft: at most once per RTT), has no reference window or bytes in flight (`OnPacketSent` is empty), no pseudo-L4S delay response, no classic-ECN vs L4S distinction, no minimum send rate or source-limited handling, RTT from the newest send time only [S] Now: `ScreamCongestionController` implements draft-01 sections 4.1-4.5 (reference window, once per min(VIRTUAL_RTT, s_rtt) reductions, loss/classic ECN/L4S/pseudo-L4S responses, LEDBAT base delay with RTT fallback, send window with the reference implementation's 0.5 s release, relaxed pacing, media rate control from sender queue delay and frame size, conditional loss and policer cap, undershoot guard, clock drift reset, competing-flow target); the pacer gates on the send window; CCFB adds the section 5 16-packet trigger. Closed-loop tests over a simulated bottleneck: 98 % use and 22 ms queue on a clean path, 95 % and 1.9 ms with L4S marking, no collapse at 0.3 % random loss, 80 % under a policer. Reference implementation in `docs/references/scream-cpp/`. | done (e0abd8b) |
| T11 | ECN is always ECT(1) with no path validation, blackhole or bleaching detection, fallback or re-probe; CE is treated as L4S whatever the network [S] Now: `a=ecn-capable-rtp: rtp` negotiated with mode and ect (RFC 6679 section 6.1); marking only when this side can set and the peer can read; `EcnValidator` (Adaptation) probes a quarter of media, validates after four intact marks, fails on bleaching, re-marking or blackholes, retries after two minutes and on ICE path changes; ECT(1) only when the peer asks for it and the controller answers scalably; SCReAMv2 detects a classic AQM (CE with a standing queue of 5 ms or more) and switches to the classic response and ECT(0) (RFC 9331 section 4.3 item 3). End-to-end tests over the in-memory network with a re-marking hook. | done (8b70516) |
| T12 | Payload size was a constant 1100 bytes; no PMTU discovery [S] | done (44eea26, 824820a): RFC 8899 DPLPMTUD in Adaptation, a port of quinn's binary search and loss-burst black hole detector (quinn's test scenarios ported with their exact probe sequences), BASE/ERROR states per RFC 8899 5.2; probes are padded RTX packets confirmed by CCFB, counted in flight but excluded from loss signals (RFC 9000 14.4); state per local/remote pair (RFC 9000 14.2); packetizers take the payload size per frame. Found and fixed on the way: DeliveryTracker never resolved packets no report mentioned (RACK time rule, RFC 8985 6.2) |
| T13 | Dtls.Core has no connection IDs (RFC 9146, RFC 9147 section 9) or record size limit, and PeerConnection pins no DTLS version; DTLS 1.3 makes padded probe records and CID-based rebinding possible [S] | done (Dtls.Core 0bbb429, d38822c): CIDs 1.2/1.3 with NewConnectionId/RequestConnectionId, RFC 9146 section 6 peer address update over an unconnected UDP transport, RFC 8449 limits; wolfSSL CID interop both directions; 144 tests green on Windows, lab, Mac |
| T14 | FlexFEC wire format is RFC 8627's (F=0 flexible mask, 15-bit, k=0 last block, protected SSRC in the CSRC) and matches libwebrtc's current reader (`docs/references/libwebrtc-flexfec_header_reader_writer.cc`: CSRC-carried SSRCs, 2/6/14-byte masks, R=1 and F=1 refused); the doc comment calling it flexfec-03 is wrong. Gaps [S]: not negotiated in SDP at all (payload type and SSRCs come from `PeerConnectionOptions` on both sides; no `a=rtpmap ... flexfec`, `a=ssrc-group:FEC-FR`, `repair-window`, RFC 8627 section 5), so only identically configured StreamTransport peers use it; one repair per fixed group, 15-packet span, single-loss recovery, no 46/110-bit masks, no interleaving (F=1 is also refused by libwebrtc) | partly: SDP negotiation done 6fe0796 (RFC 8627 `flexfec` codec, repair-window, FEC-FR group, separate send/receive config; a receive-only peer such as a WHIP server now recovers); remaining: longer masks (46/110-bit), repair rate set by the T15 policy. No flexfec-03 alias (user decision 2026-10-07: modern, RFC naming only) |
| T15 | No joint recovery policy: FEC and RTX are enabled independently of RTT, playout deadline and budget [S] | done (see the T15 commit): RecoveryPolicy in Adaptation (pure, tested): no FEC without loss or below 20 ms RTT, none for frames under 700 B below 200 ms RTT, span <= 2*fps*RTT frames (max 6), largest group with P(>=2 lost of n+1) <= 1%, floor 2 (50% overhead); retransmission always on. PeerConnection measures fps and packets/frame from protected packets and replans every 500 ms; FEC starts off and turns on with loss. Session test: 22 of 24 dropped packets recovered with no RTX negotiated |
| T16 | No network-emulation measurements: everything so far is loopback, LAN and the three-way field test [S] Unit-level closed-loop simulator (`PathSimulator`) covers bandwidth steps, L4S marking, random loss, policers and RTT-only feedback; the netem rig on the lab box remains. | partly (e0abd8b) |
| T22 | RTCP reporting is missing: no Sender Reports, Receiver Reports, SDES CNAME or BYE are ever sent (`RtcpSenderReport`/`RtcpReceiverReport` only parse); feedback goes out non-compound while `a=rtcp-rsize` (RFC 5506) is never offered. RFC 3550 section 6 and RFC 8834 section 4.1 require them; browsers use the SR's NTP/RTP pair for A/V sync and RR blocks for loss, jitter and RTT (LSR/DLSR); the CCFB RTS must share the SR's clock (T7). Also fixed: a fixed CNAME on every connection (now 96 random bits per connection, RFC 7022) and an unsigned read of the 24-bit cumulative-lost field [S] | done |
| T23 | CCFB was never negotiated: no `a=rtcp-fb:* ack ccfb` offered or read, and feedback was sent regardless (RFC 8888 section 6) [S] | done (a53614e): offered as the wildcard, per-codec form accepted, answered only when offered, sent only when agreed; `rtcp-rsize` follows the same answer rule |
| T24 | Every datagram went out ECT(1): Linux and macOS set a socket-wide TOS and Windows marked each send, so STUN, DTLS and RTCP were ECT; RFC 6679 section 7.3.1 forbids it for RTCP and advises against it for the rest [S] | done (8b70516): per-datagram marking through `sendmsg` with an IP_TOS / IPV6_TCLASS cmsg (draft-ietf-tsvwg-udp-ecn-08 section 4.2.1) and `WSASendMsg`; only RTP media carries ECT |
| T25 | Media mobility is break-before-make: `TriggerRecovery` clears the selected pair on every address-change event, so even an unrelated event (IPv6 privacy rotation) stops media until checks pass again. It should keep sending on the current pair and switch only once an alternate is validated [S] | done (d9847da: TriggerRecovery re-checks without clearing the selection; switches only to a better writable pair) |
| T26 | A silent path death (cellular dead zone, CGNAT rebinding, no interface event) is detected only by consent at 30 s, and standby pairs ping at the 5 s consent cadence. Liveness should come from the RTCP the media already brings (CCFB every 25 to 250 ms): silence for a few feedback intervals fails over to a warm pair; standby keep-warm runs faster on a multihomed field device [S] | done (d9847da: receiving from any data within 2.5 s, libwebrtc ping cadence and ranking with 1 s damping; failover 3.6 s in the switchboard vs 30 s) |
| T27 | An interface that appears mid-session (the 5G modem attaches, Wi-Fi joins) gets no candidate until a full ICE restart through signaling. Gather on the new interface incrementally, check it against the known remote candidates (a public relay learns it as peer-reflexive, no signaling needed) and trickle it when a signaling channel is up [S] | done (fae5647: continual gathering on a network change, removal of vanished addresses with immediate failover; peer learns the new address as peer-reflexive without signaling; bc6c746 fixed triggered checks bouncing at the pacing rate on valid pairs, 2400 checks/min idle, which also kept standby pairs from ever being checked) |
| T28 | A path switch keeps the congestion state of the old path: SCReAM's base delay, ref_wnd and RTT belong to Wi-Fi when the media moves to 5G. On a switch the controller restarts from the standby pair's measured RTT, the pacer keeps its queue, and the receiver gets no spurious keyframe request for the switch gap (NACK/FEC cover it) [S] | done (7a75175: congestion control and RTT restart on new addresses, kept on port-only change (RFC 9000 9.4), seeded from the standby pair's RTT; session test checks no keyframe request) |
| T29 | P2P mobility needs signaling mid-session, and WHIP/WHEP answer PATCH with 405: no trickled candidates (RFC 9725 `application/trickle-ice-sdpfrag`) and no ICE restart. A home StreamWeaver behind a port-restricted NAT drops checks from a new cellular address it never sent to, so the agent's new candidate has to reach it over the HTTP signaling channel (which DevTunnels carries fine). IPv6 is the primary answer (cellular and most home links have global addresses, ranked first by RFC 8421 already); a stateful IPv6 firewall still drops unsolicited checks, so both sides must learn each other's new addresses and check simultaneously. Implement PATCH trickle and restart on the server and the client. TURN stays available but is not the plan [S] | done (932bb1a: WHIP/WHEP clients PATCH late candidates as trickle-ice-sdpfrag with If-Match, server adds them (204/428/412/415/400/404); 07ea78d: ICE restart over PATCH both ways, 200 with new credentials, candidates and ETag; IMediaSession.RestartTransportAsync; live WHIP restart test keeps media flowing) |
| F20 | A local ICE restart closes every socket and pair before the new ones check, so media stops until the restarted session connects (break-before-make); libwebrtc keeps the old generation sending until a new pair is writable. Mobility no longer depends on restarts (T25-T29), so this only costs the rare full restart | open |
| F21 | No end-to-end test of an interface coming up inside a WHIP session and its candidate reaching the server by PATCH: each half is tested (fae5647 agent, 932bb1a channel); needs a swappable socket factory in media sessions, or the field test (V1) | open |
| F22 | PCP (RFC 6887) to open an inbound IPv6 pinhole on a home router, for when the server cannot check out first (it can with trickle, so optional) | evaluate |
| T32 | Two non-bonded modems: send FEC repairs and retransmissions of packets lost on the main pair over the warm standby pair (path-diverse repair), so a burst on one modem is repaired via the other; receiver already accepts any validated pair. Multipath QUIC (draft-ietf-quic-multipath-21) is the standards-track model | done: IceStateMachine.Standby (best writable pair on another local endpoint answering keep-alives), IceAgent.TrySendOnStandbyAsync, PeerConnectionOptions.RepairOverStandby / MediaTransportOptions.RepairOverSecondPath (on in the IRL profile); retransmissions and FEC repairs go there, out of the media path's congestion control and feedback (multipath QUIC's per-path model); controlled side now ranks by most recent nomination before newest data so repairs on the standby do not pull its selection. Tests: switchboard (selections stay), session (39 of 40 dropped packets recovered over the other interface). Metric streamtransport.webrtc.repair.standby |
| A1 | Side-by-side alpha pack checked the doubled width only, so odd colour widths put a 4:2:0 chroma block across colour and alpha (D3D12, Vulkan, Metal; FFmpeg CPU processor was already right) [V] | done d8879b8 (+ D3D12 test) |
| A2 | Packed alpha is always mapped to video range (16..235) and unpacked as video range, but the processors may accept a full-range packed output: alpha 0 would decode to ~16/255. Make packed side-by-side always limited range [report, unverified] | done: verified as a cross-path bug, not the report's version. GPU shaders map alpha through the frame's range (grey through the same matrix, unpack normalised like the colour); the CPU processor always used 16..235, so a full-range frame packed on one path unpacked wrong on the other. CPU now follows the frame's range; test Pack_FullRangeFrame_CarriesAlphaAsFullRangeLuma |
| A3 | Chroma of the packed/converted NV12 is a 2x2 box average (centre-sited); H.264's default chroma location is left. Decide and document; compare against swscale as oracle [report, plausible] | documented: the GPU processors' 2x2 average is centre-sited chroma, stated on VideoProcessing.Color; signalling it in the encoders' VUI (chroma_sample_loc) or switching to left-sited taps is open, pending a swscale-oracle comparison |
| A4 | GPU conversion applies matrix and range only; VideoColor primaries/transfer are carried as metadata, not converted (BT.2020/PQ/HLG -> BT.709 is not a real conversion). Document the contract or add RGB->RGB primaries and transfer handling [report, plausible] | documented: VideoProcessing.Color states the processors convert matrix and range only; primaries and transfer are carried, so HDR or wide-gamut sources are mapped before (a real RGB-to-RGB conversion is open if a use case needs it) |
| A5 | Vulkan processor creates image views and allocates/updates/frees a descriptor set per frame (VulkanVideoProcessor ~401-433, views destroyed ~592) [V]; cache views and use a descriptor ring | done [S]: views are made once per plane and format and destroyed with the image; each kernel reuses its descriptor sets once the batch that bound them has run, so a frame only updates a set. Compiled and formatted on a cloud Linux box (llvmpipe lacks the DRM modifier, DRM device and semaphore-fd extensions, so the Vulkan tests skip there); the lab box run verifies it |
| A6 | D3D12 processor writes R8/R8G8 intermediates and copies them into the NV12 resource, a second full-frame copy [report, unverified] | done: where CheckFeatureSupport reports typed UAV store for NV12 (RTX 3060: yes), the pool makes the NV12 output shader-writable and the compute pass writes its two planes through plane-slice UAVs, no intermediates, no copy; elsewhere the R8/R8G8 + copy path stays. Test Nv12Output_IsWrittenInPlace_WhereTheDeviceStoresNv12; GPU alpha session tests confirm the D3D12 encoders take the UAV-flagged NV12 |
| A7 | Alpha half of a packed frame needs no RGB matrix: Y = 16 + 219a/255, chroma 128; special-case the blocks [report] | optional |
| A8 | FFmpeg pinned at BtbN ffmpeg-n9.0.1-11-ge47273f4d9 (eng/fetch-ffmpeg.ps1) [V]; report says 9.0.2 is out (2026-09-18) [unverified] | todo: bump once a BtbN 9.0.2 build exists |
| A9 | VideoToolbox HEVC encodes the alpha layer (alpha_quality) but DecodesAlphaLayer is software-only (FFmpegVideoDecoderFactory.cs:209) [V]: Mac-to-Mac native alpha only decodes in software. Use VideoToolbox's own hevcWithAlpha hardware decode; make AlphaLayout.Layer codec-neutral (HEVC AUX_ALPHA now, VVC AUX_ALPHA and AV2 ALPHA_AUX later) | todo |
| A10 | x-alpha is advertised from the application's offer, not derived from the chosen encoder/decoder capability (WebRtcMediaTransport ~428) [V partially]; Layer must be negotiated only when encoder and decoder really do it | not a defect [V]: MediaSession.AlphaWays offers Layer only when the registry has an encoder (CanEncodeAlphaLayer) or decoder (CanDecodeAlphaLayer) of it, and encoder/decoder selection filters on EncodesAlphaLayer/DecodesAlphaLayer when Layer is negotiated (MediaCodecRegistry 109, 142, 216). The real gap is A9 (VideoToolbox hardware decode) |
| A11 | QSV HEVC alpha_encode exists in FFmpeg (Windows, D3D11 RGBA) but no matching hardware decode; defer until a decoder exposes the layer [report] | deferred |
| C1 | Codec roadmap: VVC/H.266 (RFC 9328 RTP payload; FFmpeg 9 native vvc decoder, libvvenc encoder; check realtime viability in software and alpha via AUX_ALPHA/H.274), AV1 complete (RTP spec, SVC), AV2 prep (ALPHA_AUX). Compare and decide with measurements | todo: research |
| F23 | FlexFEC copied each protected packet's payload on both sides | done: send side accumulates parity in place (2f102e5); receive side keeps the peer's last 256 protected packets in a fixed ring of reused 1500-byte slots indexed by sequence modulo 256, no allocation per packet (one copy remains, since receive buffers are reused) |
| F15 | `WasapiTests.SinkToLoopbackSource_CarriesTheToneWithCaptureTimes` depends on the machine's output volume: it reads the tone back through the default output's loopback and fails when the output is muted or low (0.0007 RMS seen 2026-10-07). Read the endpoint volume and mute state and make the test inconclusive when the loopback cannot carry the tone [S] | open |
| F16 | Path MTU: PTB messages (ICMP Packet Too Big) are not used; RFC 8899 4.6 makes them optional and they would need validation against the quoted packet. The relayed-pair maximum reserves the worst TURN framing (52 bytes); a pair on ChannelData could use 48 more [S] | open |
| T17 | DSCP: not set anywhere; orthogonal to ECN, an optimisation only [S] | deferred (after T11) |
| T18 | UDP Options (`draft-ietf-tsvwg-udp-options-45`) and its DPLPMTUD draft: watch, no dependency [S] | deferred |
| T19 | ECN through tunnels (RFC 9601) and CONNECT-UDP: covered by treating ECN as a path property (T11) [S] | with T11 |
| T20 | Windows ECN: done and verified on all three machines (Z27) [V] | done |
| T21 | QUIC, QUIC DATAGRAM (RFC 9221), RTP over QUIC (`draft-ietf-avtcore-rtp-over-quic-14`), MoQ (`draft-ietf-moq-transport-22`, `draft-ietf-moq-loc-04`): evaluate once T1-T3 land; no implementation in this restructure [S] | evaluation (after step 3) |

## Follow-ups

| # | Item | Status |
|---|---|---|
| F1 | Audio resampling processor (sources and sinks at rates the codec does not take) | todo |
| F2 | ADR-115 capture vs observation reaches the receiver only as abs-capture-time (no flag) | todo |
| F3 | Answers that match no codec echo the offer instead of rejecting the m-line (RFC 3264 section 6) | todo |
| F4 | PeerConnection tests on real loopback; move them to the in-memory network | todo |
| F5 | Borrowed ref structs held across an `await` under runtime async (CS4007 off): analyzer or design | todo |
| F6 | `FFmpegCodecOptions.LoggerFactory` is a service in an options object | todo (end review) |
| F7 | Media Foundation camera D3D11 texture path (C6) | todo |
| F8 | PipeWire.NET tests poll with Task.Delay | todo |
| F9 | Coverage: merge the GPU machines' runs with CI's legs into one figure per repo; try lavapipe on Linux runners for part of the Vulkan code | todo |
| F10 | NVENC alpha layer (FFmpeg exposes none), hardware decode of alpha layers: upstream | deferred |
| F11 | Upstream reports for the user: FFmpeg Z6/Z8; PipeWire bind window, update_permissions, trigger invoke after disconnect and busy push-back (Z28) | user's call |
| F13 | Circuit breaker gaps: a receiver whose RRs stop carrying a block for our SSRC (forward path dead, return path alive) never trips the media timeout | open (session surfacing done in f6f8041) |
| F14 | Interop check against a browser: whether current Chrome offers `a=rtcp-fb:* ack ccfb` by default is unverified (libwebrtc's generator reads its tuning from the `WebRTC-RFC8888CongestionControlFeedback` field trial, [S] `docs/references/libwebrtc-congestion_control_feedback_generator.cc`). Without it this sender gets no CCFB and SCReAM no feedback; check on the rig in step 8 and fall back to RR-based loss if needed | open |
| F12 | Published by mistake on 2026-10-06 by a workflow dispatch on main: PipeWire.NET and PipeWire.NET.Media 0.3.0-dev2.15, Dtls.Core 0.0.0-alpha.0.16; kept as they are (user, 2026-10-06); proper previews will follow | closed |

## Done

| # | Component | Notes |
|---|---|---|
| A1-A2 | Media: frame model, storage union, time, codec contracts, ids, D3D12 storage, tuning, per-plane CPU frames | `e67bcea`, `d49f77f` |
| B1-B8 | Codecs.FFmpeg: one encoder/decoder core; software; D3D12VA + D3D11 (NVENC/AMF); VAAPI; VideoToolbox; Vulkan (DMA-BUF in place); factories, capability probes, runtime fallback; Opus | QSV unverified (no Intel GPU) |
| C1-C7 | Platform packages: Windows (D3D12 compute, Spout, WASAPI), macOS (Metal, Syphon, Core Audio), Linux (Vulkan, DMA-BUF, PipeWire video/audio); cameras (V4L2, MF, AVFoundation); FFmpeg capture fallback | C6 MF D3D11 path is F7 |
| D1-D3 | Composition, MediaClock/TimeProvider, device affinity | |
| E1-E5 | Sans-IO ICE, TURN (UDP/TCP/TLS), AES-CM SRTP, H.264/H.265/AV1 payloads, fuzzing | |
| F1 | WHIP/WHEP | |
| M1-M3 | MediaDevices registry; test signal and A/V analyzer; rate control at the measured frame rate | |
| V1 | Three-way field test over IPv6 | |
| Z | Zero-copy: Z1-Z4, Z11, Z12, Z13, Z14, Z15, Z21, Z22, Z24, Z25, Z27, Z28, Z29 | see the zero-copy table |
| P | Platform tests: `[OSCondition]` everywhere, MSTEST0061 as an error, rows from the running OS's catalogue, OS-only test projects, coverage merged across legs in StreamTransport, PipeWire.NET, Dtls.Core | |


## Sister libraries

Status as of 2026-10-06, from the code, not memory. Spout2.NET: shader-writable shared texture `4308918`. Syphon.NET: publish on a GPU event `4768290`. FFmpeg.Interop: sync-point import `14bb30b`, probe demotion on stderr `090f4ab`, Vulkan out of the runner gate `3c30481`.

| Library | Needed by | Status | Notes |
|---|---|---|---|
| Dtls.Core (was Dtls.NET) | WebRtc | done, unreleased; gaps T13 (connection IDs, record size limit, PMTU) | 1.2 + 1.3, interop tests green; OSCondition sweep and merged coverage `6e18c94`; renamed `cc05bf2`; parser and handshake fuzzing `5fd2b1e`, 50k soak green `e2d9245`; key-update test race `23733d2` |
| FFmpeg.Interop | Codecs.FFmpeg | done | CI green; gate 90/80 over runner-reachable code (D3D11/D3D12 excluded, machines cover them) `1c32eb9`; `FillBlack` `7ac3790`; GPU `Frame.CopyTo` `722d3d4`; `VulkanDmaBufImporter` `8d5b482`; Vulkan decode into DMA-BUFs `8af8e46`; spec fixes from validation `984b2cc`; generator keeps per-pass function classes `6c460d5` |
| PipeWire.NET 0.3 | Linux | done | graph API, Media package; per-format DMA-BUF offers `f9fceda`; push output for shared and memory buffers `714094f`, `8aa4f61`; teardown drain and data-loop-locked queues `0222c44`; OSCondition sweep `0c9a518`; tests still use Task.Delay (F8) |
| Spout2.NET | Windows | done | D3D12 native sharing `29b9000`; no CPU send (uploads go through the D3D12 processor) |
| Syphon.NET | MacOS | done | managed client/server, IOSurface, `PublishPixels` |

## History and design notes

Kept for the reasoning behind decisions; the plan and the open items are above.

### Original component table

| # | Component | Status | Notes |
|---|---|---|---|
| A1 | Media: frame model, storage union, time, codec contracts | done | leases shareable by default `e67bcea` |
| A2 | Media: codec ids, D3D12 storage, tuning, per-plane CPU frames | done | Yuva420 (4 planes) `d49f77f` |
| B1 | Codecs.FFmpeg: one encoder core + decoder core on FFmpeg.Interop | done | |
| B2 | Codecs.FFmpeg: software (kvazaar, OpenH264, SVT-AV1; libavcodec, dav1d) | done | |
| B3 | Codecs.FFmpeg: D3D12VA + D3D11 (NVENC/AMF) | done | QSV unverified (no Intel GPU on the three machines); QSV takes rate changes while running per FFmpeg's qsvenc (`9e740f0`), RequiresQsv test waits for Intel hardware |
| B4 | Codecs.FFmpeg: VAAPI incl. DRM-PRIME import | done | single-object planar DMA-BUFs `4c74233` |
| B5 | Codecs.FFmpeg: VideoToolbox, IOSurface in | done | H.265 alpha layer `37b98c9` |
| B6 | Codecs.FFmpeg: Vulkan encode/decode | done | DMA-BUFs imported in place as encoder input (`a4860e9`), GPU copy only for modifiers the GPU cannot encode from (logged, event 1003); Vulkan decode into exportable DRM-modifier pictures (`e5718d7`); Vulkan decode -> DMA-BUF -> Vulkan encode verified on RADV with Khronos validation + sync validation clean for our code |
| B7 | Codecs.FFmpeg: factories, capability query, runtime fallback | done | per-format surface probes, encoder refusal + fallback, GPU-first selection |
| B8 | Opus | done | |
| C1 | Windows: GPU conversion and alpha | done | D3D12 (not D3D11) compute; upload and readback `a32b105`, `0dab29a` |
| C2 | Windows: Spout source/sink | done | |
| C3 | Windows: WASAPI capture/render | done | loopback keep-alive, denied-device handling `0a8932b` |
| C4 | MacOS: IOSurface, Metal conversion, Syphon, Core Audio | done | Metal readback `0dab29a` |
| C5 | Linux: DMA-BUF, Vulkan conversion, PipeWire video/audio | done | NV12 GPU sharing `44c5e63`, monitor capture |
| C6 | Native camera capture: V4L2 (DMA-BUF), Media Foundation, AVFoundation (IOSurface) | done (MF D3D11 path open) | `6a0fa19`, `7d1eebe`, `a1a92b1`; capture timestamps from the producer; V4L2 verified on vivid (single/multi-planar, DMA-BUF into Vulkan) and v4l2loopback; MF verified on a Frame Server virtual camera end to end; AVFoundation enumerates, capture awaits a camera on the Mac; MF frames are CPU (D3D11 texture path todo) |
| C7 | FFmpeg capture device as the universal fallback source | done | `8305b78`, FFmpeg.Interop `84551e1`; MJPEG decode, dshow-only virtual cameras, device-key fallback |
| D1 | Composition: registry, send/receive streams, sessions, rooms | done | processor chains `2fda79b`; per-media failure isolation |
| D2 | MediaClock / TimeProvider everywhere | done | no Stopwatch, DateTime.UtcNow or Task.Delay left in src; confirm in the review |
| D3 | Device manager: devices derived on one GPU, affinity checked at pipeline build | done (by design) | GpuIdentity travels with each frame; send/receive streams rebuild processor/encoder when it changes; per-GPU device singletons (D3D12 runtime per adapter, Metal registry id, Vulkan engine cache); a central manager would duplicate this |
| E1 | WebRtc: sans-IO ICE core + socket driver | done | scope-ranked IPv6-first candidates `05102cc`; resilient receive loops |
| E2 | WebRtc: TURN client (UDP/TCP/TLS) | done | `6b54c05`, `0c81ab2`; channels, refresh, relay-only policy; coturn verified on all three machines over IPv6 and IPv4, three-way relay-only session |
| E3 | WebRtc: AES-CM SRTP (AES_CM_128_HMAC_SHA1_80) | done | `3a79acd`; offer order GCM-128, GCM-256, CM-80; keyed contexts, no per-packet allocation; 1024-packet replay windows; strict outbound index |
| E4 | WebRtc: H.264, H.265, AV1 RTP payload formats | done | layer ids kept (RFC 7798) |
| E5 | Fuzzing: STUN, RTP/RTCP, SDP, payload depacketizers, DTLS records | done | `7fc60e6`, `1c80187`; ParserFuzzTests + IceCandidateParseTests, STREAMTRANSPORT_FUZZ_ITERATIONS for soaks; a 1M soak found the ICE port bug; Dtls.Core fuzzed (decoders, record layer, corrupted handshakes) |
| F1 | WHIP/WHEP | done | `3e88b54`, `e3e1c5c`, `24f2582`; clients in core, endpoints in `Agash.StreamTransport.AspNetCore` on the host's routing (StreamWeaver's Kestrel, devtunnels); `Link: rel="ice-server"` advertised and discovered; Chrome WHIP and WHEP verified |
| G1 | StreamWeaver Casting / Casting.Windows / IrlAgent move | todo | drops StreamWeaver's Spout copy |
| H1 | Releases: Dtls.Core, FFmpeg.Interop, layer 0 libs, StreamTransport as -alpha | todo | after the review |
| I1 | Merge `restructure` into `main` | ready after the zero-copy work below | CI checks the sister repos out (`ci.yml`) and is green on all four runners |
| V1 | Three-way field test (Windows, Linux, Mac) over IPv6 | done | all directions, GPU rings, alpha layer + AV1 side by side |

| M1 | MediaDevices: public input/output providers, registry, fallback, adapters | done | `614f865`; Spout, Syphon, PipeWire, WASAPI, Core Audio, V4L2, v4l2loopback, MF, AVFoundation, FFmpeg, test signal |
| M2 | Test signal and A/V sync and latency analyzer | done | `410c3b6`; flash and click on whole UTC seconds, wall-time barcode; measured all six directions |
| M3 | Rate control plans for the measured source frame rate; 60 fps cap; receive backlog resync | done | `2a9a4af`, `c5505b2`, `2bdb186`; 1080p60 latency 151 to 30 ms, A/V offset -138 to -11 ms |

### Requirements from the other ADRs (StreamWeaver docs/adr)

| Source | Requirement | Lands in |
|---|---|---|
| ADR-112 s3 | Surface ownership contract: frame says who owns handles, released once | A1 done (lease/retainer); PipeWire retainer in C5 |
| ADR-112 s4 | Encoder capability query, selection on (storage, format, capability) | B7 |
| ADR-112 s5 | VAAPI DRM-PRIME importer wired to the PipeWire source; delete the drop | B4 + C5 |
| ADR-112 s6 | Publish side on the same model; conversion per storage domain; device affinity checked | C1, C4, C5, D1 |
| ADR-112 keep | thin source/sink/encoder interfaces, Spout shared device, Syphon IOSurface, PipeWire modifier negotiation, D3D11 conversion, CPU fallback, timestamp-delta RTP clocking, platform construction boundary, source-driven cadence | all |
| ADR-114 | Native capture: V4L2 (DMA-BUF), Media Foundation (D3D11), AVFoundation (IOSurface); FFmpegCaptureDevice stays as fallback | C3/C4/C5 + B |
| ADR-114 | Camera and Syphon converge on one VideoToolbox path; V4L2 and PipeWire converge on DMA-BUF | C4, C5 |
| ADR-114 | No backend is done until verified end to end on its hardware (VT, QSV, VAAPI import) | rig runs |
| ADR-115 | Producer timestamps per source (PipeWire graph clock + latency, V4L2 buffer ts, MF sample time, AVF PTS); Spout/Syphon observation time, flagged | C2-C5 (MediaTimestamp exists) |
| ADR-115 | Receiver reads the capture/observation distinction (land with the consumer) | D1 sync |
| ADR-117 | Public contracts use ImmutableArray<T>, params ReadOnlySpan<T> factories, no IReadOnlyList on data | all packages; sweep existing WebRtc/Abstractions |
| ADR-108 | AvatarTransparent side-by-side alpha, recomposited natively on all three GPUs, pixel-identical | C1/C4/C5 video processors |
| ADR-108 | Media profiles InteractiveP2P / ScreenShare / IrlContribution / AvatarTransparent | D1 |
| ADR-108 | Receive path decodes to Spout/Syphon/PipeWire sinks, zero-copy | C2/C4/C5 |
| ADR-108 | StreamWeaver is ICE controlling; IPv6-first; DevTunnels never in the library | E1, G1 |
| ADR-108 | SRT/SRTLA later behind the same transport abstraction | deferred, keep seam |
| ARCH 27.6 | GPU conversion and alpha pack/unpack in D3D11, Metal, Vulkan ship in StreamTransport | C1/C4/C5 |
| ARCH 27.7 | UseLocalStreamTransport switch; package arm must build (A-39) | G1 |
| ADR-097 P8 | Field agent: V4L2 camera, linux-arm64/x64 self-contained AOT | G1 (IrlAgent), Qualcomm research first |

### Defects from the review (D1-D11)

Tracked in `architecture-review.md`; each closes with the component that fixes it.

### Log

- 2026-09-29: DTLS on Dtls.NET (`39de563`); relay test fixes (#1) `3f6062f`, `983d521`.
- 2026-09-30: D7 done (`87faae8`). Codecs.FFmpeg encoders + decoders. FFmpeg.Interop gained colour API,
  GPU lookup by LUID/dev_t, Vulkan video caps, D3D12 fence wrap + GPU copy, device-from-texture,
  low-delay decode, IOSurface access, ImmutableArray results. Pending: B4 VAAPI, B5 VideoToolbox,
  Linux/macOS rig runs, D8 hash-pinned natives, Opus package, platform packages, composition.
- 2026-09-30: rig runs green on Windows (71), Linux AMD (40), macOS M4 (20). D8 done `bbddc12`
  (natives pinned to FFmpeg.Interop's month-end build, SHA-256, packed with Codecs.FFmpeg + LGPL text).
- 2026-10-01: Metal CPU upload `197d4c6`; WHIP/WHEP ice-server links `24f2582`; CLI stops on SIGTERM so
  published servers retire `42f08e3`; analyzer counts frames without a barcode `a6a8a1a`; explicit
  event ids per package with a test (StreamTransport `c752e27`, PipeWire.NET `b8caf26`, ObsWebSocket);
  FFmpeg's log routed to the container's logging, demoted during probes (FFmpeg.Interop `fb86e39`,
  `0d212e3`); TURN test race fixed `9d9bee0`; CSharpier pass `6627e73`. Mac camera (OBS virtual camera
  through AVFoundation): ~100 ms glass to glass from the Syphon feed, A/V -55 ms (OBS's own pipeline),
  identical on both receivers. Syphon orientation: Metal servers are top-first, OBS needs Flip
  Vertical (documented in Syphon.NET).
- 2026-10-02: H.264 profiles negotiated per RFC 6184 and encoders kept within them (`5c5728e`,
  `510c806`); D3D12's High output is non-conformant on NVIDIA (decoders disagree), so D3D12 encodes
  Main. RTX negotiated per RFC 4588 (`ac69e32`): NACK and RTX never worked between peers with distinct
  SSRCs, and the SDP reader took a browser's RTX SSRC as its media SSRC. Metrics and traces on every
  package (core, Codecs.FFmpeg, WebRtc, Signaling, Stun), logging audit of silent catches, Signaling
  and Stun given logging. Full suites green on all three machines from fresh builds: Windows 621,
  Linux 621, macOS 604 (by project). Open: answers that match no codec still echo the offer instead of
  rejecting the m-line (RFC 3264 section 6, needs a port in the SDP model); PeerConnection tests still
  use real loopback, the in-memory network now available to move them on the gate; PipeWire upstream
  patches (bind window, update_permissions) await the user's decision to send.

- 2026-10-03..06: FFmpeg.Interop CI green: QSV suite excluded on runners, coverage gated at 90/80 over
  runner-reachable code; OpenH264 `PictureTypeI` flake was uninitialized frame memory (noise overflowed
  OpenH264's buffer on a forced I-frame; 6/25 failures on 2 cores before, 0/25 after). Dtls.Core soak
  timeout and key-update race fixed. B6 rebuilt as true zero-copy (see the zero-copy section). The
  validation layer found our own engine exported multi-plane pictures as per-plane images on one
  non-dedicated allocation (VUID-vkBindImageMemory-image-01445): now one multi-planar image on a
  dedicated exported allocation (`d03ecf9`). Decoder callback state is owned by the codec context's
  SafeHandle (freed on finalisation too) `da48076`. OS-specific test projects run only on their OS via
  `TestOperatingSystem` -> `IsTestingPlatformApplication=false` (`6ccfe4c`).

### End-of-restructure review (required before H1)

Goal: consumers extend the libraries (their own encoders, decoders, video and audio codecs, RTP
payload formats, capture sources, converters, ...) without waiting on us. Apply this to all new work
as it lands; this review catches what earlier work missed.


- Pattern review over all restructure work (Media, Codecs.FFmpeg, Codecs.Opus, WebRtc, FFmpeg.Interop,
  Dtls.NET): every shared shape has an interface or template base, a registry keyed by its natural id,
  DI registration, and built-ins registered through the same path consumers use. Candidates already
  noticed: encoder/decoder factory registration, audio codec registration, capture sources, converters.
- Timing sweep: one injected TimeProvider through WebRtc (PeerConnection, IceAgent, congestion, NACK,
  pacing), sync clocks and the composition; no static Stopwatch micro clocks, no Task.Delay polling,
  no DateTime.UtcNow intervals. Fake-time tests.
- H.264 encoders must produce the negotiated profile-level-id (the payload format offers 42e01f).
- FFmpeg.Interop coverage gate: done. CI gates 90/80 over runner-reachable code; machine runs (Windows
  88.3/78.2, lab 74.1/68.2, Mac 72.3/63.5 alone) still to be merged with CI's legs into one figure.

### D1 design (composition on the Media contracts)

Extensibility first: every shape is a contract with a registry and DI registration, built-ins
registered through the same path a consumer uses.

1. Codec ids become open value types: `VideoCodecId` / `AudioCodecId` as `readonly record struct`
   over a name (well-known `H264`, `H265`, `AV1`, `Opus`), case-insensitive. The name is the RTP
   encoding name, so codec and payload format meet by name with no binding table. A consumer adds VP9
   by registering a factory and an `RtpPayloadFormat` named "VP9".
2. Audio codec factories (`IAudioEncoderFactory`/`IAudioDecoderFactory`, capability query + rank)
   mirroring video; Opus implements them. Loss recovery beyond concealment (Opus in-band FEC) is an
   optional capability interface on the decoder.
3. Video processors (`IVideoProcessor`/`IVideoProcessorFactory`): conversion, scaling, alpha
   pack/unpack between a storage/format pair on one device. Platform packages implement GPU ones
   (C1/C4/C5); Codecs.FFmpeg ships a CPU fallback on swscale.
4. `MediaCodecRegistry`: encoder/decoder/processor factories from DI, chosen by capability and rank.
5. Send path per track: source pushes, a one-slot latest-frame queue (drop-oldest, leases disposed)
   feeds an encode worker, the encoder pushes access units to an RTP stream (payload format
   packetizer, RTP timestamp from the frame's `MediaTimestamp`, abs-capture-time from capture times
   only), which queues packets on the `RtpPacer`. Keyframe requests (PLI) and bitrate estimates
   flow back from the connection. No polling anywhere.
6. `RtpPacer`: audio and video queues, audio first; a token bucket at the controller's pacing rate
   on the injected `TimeProvider`; the send loop waits for work, and when short of budget waits
   exactly the deficit. Tested on fake time.
7. Receive path per track: RTP into `RtpFrameBuffer` (video) or a reorder window (audio), keyframe
   requests out, a decode worker, playout scheduling on `MediaClock` with sender capture times
   (ADR-115 capture vs observation), then the sink (`IVideoFrameConsumer`/`IAudioFrameConsumer`).
8. `MediaSession` = one `PeerConnection` + tracks; SDP codecs are the intersection of the registries
   in profile preference order. `MediaPublisher`/`MediaSubscriber` over rooms stay the entry points,
   taking Media sources and consumers. `AddStreamTransport()` registers everything.
9. Legacy removal: Abstractions' codec/media interfaces, `Agash.StreamTransport/Codecs/*` (GPU code
   to platform packages C1/C4/C5, the rest deleted), `WebRtcMediaSender`/`Receiver`, FFmpeg.AutoGen.
10. End-to-end test: two sessions over loopback PeerConnections, synthetic source to sink, video and
    audio, timestamps checked.

Order: 1, 2, 3, 4, 6, 5, 7, 8, 10, 9; then C1-C5 and G1.

### D1 progress (2026-09-30)

- Legacy pull-based composition removed; the new one is sessions (`IMediaSession`/`IMediaSessionFactory`,
  WebRTC implementation), push streams, `RtpPacer`, typed sync (`NtpTime`, `PlayoutTimeline`,
  `PlayoutScheduler`), `MediaCodecRegistry`, rooms (`MediaPublisher`/`MediaSubscriber`), DI
  (`AddStreamTransport`, `AddFFmpegCodecs`, `AddOpusCodecs`).
- The Agent sample (Spout, Syphon, PipeWire, WASAPI, CoreAudio capture and publish, self-tests) was
  removed with it; port the platform code into C1-C5 from `e5616c9:samples/StreamTransport.Agent`,
  and the legacy GPU converters/alpha shaders from `e5616c9:src/Agash.StreamTransport/Codecs`.
  The sample returns as a thin CLI over the platform packages.
- Follow-ups: audio resampling processor (sources/sinks at rates the codec does not take, 44.1 kHz for
  Opus); ADR-115 capture vs observation reaches the receiver only as abs-capture-time today (no flag);
  TURN URLs are skipped until E2; `FFmpegCodecOptions.LoggerFactory` is a service in an options object
  (end review).
- End review: `VideoFrame` and the other borrowed ref structs can be held across an `await` under
  runtime async (CS4007 is off) and come back zeroed; a consumer test did exactly that. Look for an
  analyzer or a design that makes it a compile error again.
- C2 Spout needs Spout2.NET's unreleased Direct3D 12 sharing (`29b9000`); released with H1.
- No `Task.Delay` anywhere (user, 2026-09-30): library timing is `PeriodicTimer`/deadline waits on
  the injected `TimeProvider`; pacer and playout decisions are pure and tested with explicit times;
  tests await signals. Last one left: the ICE `SimulatedTime` settle, removed by E1 (sans-IO ICE
  agent driven by explicit time and datagrams), scheduled right after C3.

### Blocker before any release: the Dtls.NET package id is taken (found 2026-09-30)

nuget.org already has `DTLS.Net` 1.0.19 to 1.0.21 (Delme Thomas, .NET Framework, BouncyCastle 1.8.1).
Ids are case-insensitive, so our `Dtls.NET` can never be pushed under that id, and any build without
the local clone (CI, the lab boxes, consumers) restores the unrelated package: NU1603/NU1701/NU1902.
Needs a new PackageId for our library (user decision), then `Directory.Packages.props` follows it.
Box runs pass `-p:DtlsNetProject=~/Dtls.NET/src/Dtls.NET/Dtls.NET.csproj` until then.

### E1 done (5efddea)

ICE is `IceStateMachine` (sans-IO) plus `IceAgent` as its driver. Switchboard tests are
deterministic; the last Task.Delay is gone. 141/141 WebRtc tests on Windows (6 ECN skips), Linux, Mac.

### C4 macOS package (2026-09-30)

`Agash.StreamTransport.MacOS` (net11.0-macos when `BuildMacOS`, empty elsewhere): Metal processors on
IOSurfaces (BGRA to NV12 with side-by-side alpha, NV12 to BGRA), CVPixelBufferPool surfaces, Syphon
source and sink, Core Audio source and sink on AVAudioEngine, `AddMacOSMedia()`. The Windows package
gained `AddWindowsMedia()`; its D3D12 processor was never registered before. Tests 14/15 on the M4
(the live microphone test is inconclusive: the Mac mini has no input device; `CapturedAudio` covers
conversion and timing with synthetic buffers).

Open: Core Audio capture on a machine with a microphone; RGBA IOSurface input deliberately unsupported
(nothing on macOS makes it).

### Release order (package arms are broken until these ship)

StreamTransport CI resolves owned libraries from nuget.org. These must be released first, in order:
- Dtls.NET: needs a new PackageId (collision above).
- FFmpeg.Interop: pinned `0.1.0-alpha.1`, nuget.org has only `0.1.0-dev1`.
- Spout2.NET: D3D12 sharing is unreleased (29b9000).
- Syphon.NET: the managed protocol API (434735a onward) is unreleased; `0.1.0-alpha.7` on nuget.org is
  the native-shim API, so the macOS CI job cannot build until it ships.

### C5 Linux package and PipeWire.NET fixes (2026-09-30, committed: StreamTransport bb39c9a, PipeWire.NET d06bfcc on branch streamtransport-gpu)

Direction from the user: GPU zero-copy is the primary path for shared video (PipeWire/OBS/VTube
Studio, Spout, Syphon), cameras are the CPU exception; sister-lib gaps are fixed in the lib.

`Agash.StreamTransport.Linux`: PipeWire video source (DMA-BUF shared on our GPU when the consumer takes
it, memory fallback in BGRA/RGBA/NV12/I420, GPU copy on retain), PipeWire video sink (DMA-BUF buffers
we export, one GPU copy per fill; memory staging otherwise; colour declared), PipeWire audio
source/sink (OutputLatency from PipeWire's playback latency), Vulkan engine (Vortice.Vulkan, SPIR-V
via glslc committed), Vulkan processor (BGRA/RGBA<->NV12, side-by-side alpha), DMA-BUF pool, staging
transfers, `AddLinuxMedia()`. Vulkan processor 6/6 on RADV.

PipeWire.NET changes (full build/verify-linux.sh harness green on both TFMs, private and live legs, no leaks):
- `PipeWireStreamTime` + `Time` on every stream, `PipeWireAudioOutput.PlaybackLatency`.
- `VideoChromaSite`, `VideoColorInfo.ChromaSite`, BT.601 primaries, BT.601/PQ/HLG/linear transfer,
  colour parsed and declared (`PipeWireVideoOutput(color:)`).
- Planar host memory: capture takes one block or one per plane (Blocks as a range), exposes every plane
  (`HostPlaneCount`/`GetHostPlane`/`GetHostStride`), MemFd frames count as mapped; `Clone` and pulled
  frames keep every plane. Before this, PipeWire.NET could not stream NV12/I420 to itself, and a
  GStreamer planar producer lost its chroma.
- DMA-BUF capture with a host-memory fallback in several formats (first preferred format on the GPU).

Open, to do in PipeWire.NET: the DMA-BUF output has no host-memory fallback (a memory-only consumer
cannot link a GPU node); DRM syncobj helpers are internal (explicit-sync frames from producers other
than the PipeWire capture cannot be waited on here); the test suite still uses Task.Delay; tests run
with plain `dotnet test` collide across TFMs (use build/verify-linux.sh).

Open in StreamTransport: imports are made per frame (cache by buffer), the sink copies a retained
PipeWire-source frame twice (retain + fill), NV12 DMA-BUF capture is not offered (BGRA only on the
GPU), no Vulkan explicit-sync (sync_file) interop.

Mistake to own: `git reset --hard` on the lab box's PipeWire.NET clone discarded uncommitted edits to
build/fetch-upstream.sh, build/session-snapshot.py and build/session.sh (possibly only line endings
from earlier syncs; not recoverable).

### AV1 alpha (design note, 2026-09-30)

The user recalled AV1 carrying alpha natively. It does not: the AV1 bitstream has no alpha plane.
AVIF stores alpha as a separate monochrome (4:0:0) AV1 image, and WebM carries VP9/AV1 alpha as a
second stream in BlockAdditional; neither has an RTP form. Options for AV1: keep side-by-side (one
stream, one encoder, sync for free; the alpha half's chroma is neutral and costs almost nothing), or
a second monochrome AV1 stream on its own SSRC, synchronised by RTP timestamp (saves little over
side-by-side, costs a second encoder and a receiver-side pairing). Recommendation: side-by-side for
every codec; a monochrome alpha stream only if measurements show a real gain.

Verification at bb39c9a: Windows 427 (298 pass, 129 skip), Linux 427 (262 pass, 165 skip), Linux package
16/16 on the box (GPU loopback stays DMA-BUF end to end), macOS package 14/15 (no microphone).
PipeWire.NET also: frames whose chroma cannot be read are handed on with the readable planes
(GStreamer pipewiresink reports odd-height I420 chroma past its block: upstream quirk worth reporting).

### Dtls.Core and branch merges (2026-09-30)

The DTLS library is now **Dtls.Core** (user's choice; no personal-name prefix): package, assembly,
namespaces, projects, GitHub repo Agash/Dtls.Core (cc05bf2), local clone C:
epos\Dtls.Core.
StreamTransport follows at 78c5b0b (`DtlsCoreProject`/`UseLocalDtlsCore`). The release-order blocker
on the id is closed.

PipeWire.NET's streamtransport-gpu branch is merged into main (c499c1b), including the DMA-BUF
output's host-memory fallback (`HostMemoryFallback`), which the Linux sink uses to read GPU frames back
for memory-only consumers (0cdad85). DMA-BUF imports are cached across frames (e18ad2b).

Releases: allowed as -alpha once a sister library is practically finished; held until the end-of-goal
pattern review has been through each library, so a published surface is not renamed right after.

### Three-way field test (2026-10-01): open follow-ups

- **NVENC alpha layer.** NVIDIA's Video Codec SDK 12 can encode HEVC with an alpha layer, but FFmpeg's
  `hevc_nvenc` does not expose it (no option; BGRA input drops alpha). An upstream FFmpeg patch would
  give Windows a GPU alpha-layer encoder; today only VideoToolbox encodes the layer.
- **Hardware decode of the alpha layer.** FFmpeg's hwaccel paths decode the base layer only, so a layered
  stream decodes in software. Fine for avatar sizes; revisit if FFmpeg adds multi-layer hwaccel.
- **Vulkan encode from DMA-BUF.** `h264_vulkan` still takes memory only (RADV faulted on mapped
  DMA-BUFs); Linux zero-copy encode goes through VA-API. Fix the Vulkan path so the preferred API is
  zero-copy too.
- **Probe log noise.** Encoder probes now try each surface format, and FFmpeg logs every refusal at error
  level ("Codec sequence initialisation failed", "No usable encoding profile"). Silence FFmpeg's log
  during probes.
- **Flaky test.** FFmpeg.Interop `PictureTypeI_ForcesAKeyFrameWhereAsked` failed once in a full parallel
  Linux run (`avcodec_send_frame` unknown error); passes alone 5/5. Investigate under load.
- **History.** Resolved: the CPU packing commit was rewritten to build on its own (force-pushed).
- **AV1 alpha on the GPU.** Side by side end to end: shader pack (D3D12, Vulkan, Metal), AV1 encode,
  GPU decode, shader unpack. None of the three test machines encodes AV1 in hardware (RTX 3060,
  Radeon 680M / VCN 3, Apple M4 all decode only), so there the packed frame is read back for SVT-AV1;
  GPU AV1 encoders already in the catalogue (NVENC on Ada, AMF/VA-API on RDNA3, QSV on Arc, D3D12,
  Vulkan) take it straight from the GPU where present. Untested on such hardware here.

### Zero-copy (2026-10-05..06): principle, audit, plan

Principle (user): zero-copy for all modern hardware and OS configurations, native stacks first (D3D12,
Vulkan with DMA-BUF, Metal/IOSurface/VideoToolbox); FFmpeg where it gives the codec APIs, bypassed where
its abstractions force a copy; a GPU copy only as a scoped fallback where the platform cannot share
memory; cameras delivering CPU memory are the accepted exception. Do it structurally right: refactor and
restructure wherever needed, greenfield.

Independent audit (Sonnet agent, read/execute only, all three machines) findings, marked [V]erified,
[S]ource/spec, [A]ssumed:

| # | Finding | Status |
|---|---|---|
| Z1 | Encoder prepares the original frame but retains `frame.Retain()`; for PipeWire, Spout, Syphon sources Retain is a per-frame GPU copy that does not even protect what the encoder reads (FFmpegVideoEncoder.cs:125,130) [S] | done: encoder retains first and prepares from the lease; PipeWire holds buffers, cameras hold CVPixelBuffers |
| Z2 | Importer used MUTABLE/EXTENDED flags without a format list (VUID 02313/02353) [V] | fixed `984b2cc` |
| Z3 | Importer images carried usages beyond the profile's supported set (VUID-vkCmdEncodeVideoKHR-08206, found in the follow-up run) [V] | fixed `984b2cc`: encoder imports are ENCODE_SRC only |
| Z4 | GPU copy left TRANSFER_WRITE access on the frame (VUID 03915) [V] | fixed `984b2cc` |
| Z5 | Acquire uses oldLayout GENERAL for an image created UNDEFINED (VUID-01197, not flagged by the layer) [S] | keep: the DMA-BUF convention (wlroots, gamescope); revisit with VK_EXT_external_memory_acquire_unmodified |
| Z6 | 3 leaked VkImageViews per Vulkan encode [V] | FFmpeg 9.0.2 encoder: same on FFmpeg's own pool path; check FFmpeg master, upstream report |
| Z7 | FFmpeg 9.0.2 upload leaves TRANSFER_WRITE into the encode barrier (VUID 03915) [V] | fixed in FFmpeg master (vulkan_encode.c source barrier uses ACCESS_2_NONE); arrives with the next FFmpeg pin |
| Z8 | FFmpeg puts the dedicated DPB on DRM-modifier tiling when the output pool uses it; RADV supports DPB optimal only (VUID 06811/08334) [V] | FFmpeg bug (vulkan_decode.c dpb init); upstream patch to prepare, user sends |
| Z9 | Copy fallback builds a VulkanQueueWork per frame and re-maps through FFmpeg each frame [S] | todo: long-lived copier in the input |
| Z10 | Windows 1080p transcode always copies: decoded textures are 1088/1152 tall, D3D12Input copies unless exact [V]; h264_d3d12va accepts 1088 in place on NVIDIA, HEVC does not [V] | todo |
| Z11 | D3D11 copy before NVENC/AMF unnecessary: both accept foreign textures on the same device bit-identically [V] (NV12; BGRA untested) | done: `WrapD3D11Texture` for exact-size frames on the encoder's device [V NVENC, AMF] |
| Z12 | D3D12 processor retains (Spout retainer = CopyTextureRegion); Metal and Vulkan processors finish before returning [S] | D3D12 done (sources lend through `D3D12Sync.ReleaseQueue`, read in place [V]); Metal: Z21; Vulkan: Z13 |
| Z13 | Linux GPU hand-offs rely on CPU fence waits (VulkanEngine.Execute waits after every batch; importer Release blocks); RADV-allocated DMA-BUFs are explicit-sync, so an importer does not wait implicitly [V] | in progress: explicit sync end to end (see Z13 design below) |
| Z14 | macOS camera IOSurface frames have no retainer; the encoder's Retain throws, so camera -> VideoToolbox fails [S] | done: camera frames hold their CVPixelBuffer (`PixelBufferRetainer`) [V Mac, OBS virtual camera] |
| Z15 | Sinks copy into shared surfaces: Spout `Send`, PipeWire `FillShared`, Syphon blit [S] | done: `IVideoSink.TryRender` lends the sink's surface, `IVideoProcessor.TryProcess` draws into it; receive path converts at presentation (Presenter) and decoders allocate playout's held frames (`VideoConstraints.HeldFrames`, FFmpeg `extra_hw_frames`). Spout (D3D12, release-queue ordered, Win [V]), Syphon (Metal, Mac [V]), PipeWire (Vulkan into pushed buffers, PipeWire.NET `PushFrames`/`TryBeginFrame` with at most one frame waiting [V lab]). `Render` returns Rendered/Skipped/Unavailable so a busy sink costs no conversion |
| Z16 | NVENC/AMF/QSV in FFmpeg take no D3D12 input (amfenc.c, nvenc.c, qsvenc.c: no D3D12 at all) [S] | the D3D12 path is D3D12 Video encode (reaches each vendor's encoder through its driver), verified zero-copy on NVIDIA; native D3D12 NVENC/AMF only via their SDKs: evaluate after Z1-Z15 |
| Z17 | AMD iGPU D3D12 encode fails "Failed to check rate control support" [V] | driver 30.0.15002.1004 dated 2022-03-09 predates D3D12 video encode support: user to update the AMD driver, then rerun `D3D12Encode_*` (Amd rows) |
| Z18 | NVIDIA on Linux unverified (no box) | try WSL2 on the Windows machine: CUDA/NVENC pass through; native Vulkan Video and DMA-BUF are not available in WSL (dozen/d3d12 only), so only the NVENC-on-Linux path is testable there |
| Z19 | Per-plane imports of tiled NV12 into the Vulkan processor are only valid for modifiers with independent planes [A] | todo: import multi-planar pictures as one image with plane views (mirror of the export fix) |
| Z20 | NVENC on Linux takes DMA-BUFs through system memory (catalogue: D3D11 storage only); QSV has no GPU storage | todo after Z18 |
| Z21 | macOS waits on the CPU: Metal processor `Complete`, Syphon render (publishes when the renderer returns), Syphon client blit + `WaitUntilCompleted` [S]. Syphon's own Metal server publishes from a completion handler, so nothing requires a wait | done: Metal stages hand results on from command-buffer completion in commit order (`CommandCompletions`), inputs kept until then; Syphon render targets carry the sink's MTLSharedEvent and Syphon.NET publishes from its listener (`SyphonServerFrame.Publish(event, value)`); Syphon source copies each new frame once and delivers it finished, so VideoToolbox never sees an unfinished surface [V Mac] |
| Z22 | PipeWire video output in memory is pull only: the sink stages a CPU frame, then copies it into the daemon's buffer (two copies) [S] | done: `TryBeginFrame` hands out memory buffers too (`Pixels`, `Stride`, `IsPushing`); the sink is push-only, no staging, no retained latest frame [V lab] |
| Z23 | A decoded frame already in the PipeWire node's format is still copied into the shared buffer [S] | evaluate: decode into the sink's buffers (FFmpeg Vulkan frames pool from the sink); FFmpeg's output doubles as DPB, so likely not reachable |
| Z24 | PipeWire sink lock order: the loop thread takes the sink lock under the loop lock; the sink must never take the loop lock while holding its own [S] | documented on `Render`; keep, covered by the push tests |
| Z26 | ARM codec engines. Verified in FFmpeg 9.0.2 source: upstream `h264/hevc_rkmpp` encoders and decoders take and give DRM PRIME on the `DRM` device type (zero-copy, `rkmppenc.c:67,531`, `rkmppdec.c:162,548`); `*_v4l2m2m` (H.264/HEVC encode and decode, no AV1) use `V4L2_MEMORY_MMAP` only (`v4l2_buffers.c:523`, `v4l2_context.c:380,447,730`), so every frame crosses the CPU. Qualcomm Venus/Iris and MediaTek are V4L2 M2M (MediaTek decode stateless, Request API). External review (2026-10-06) agreed on API-named backends and DMA-BUF as the one surface; its v4l2m2m-is-zero-copy assumption is wrong | todo before the field agent: `Rkmpp` encoder and decoder backends (DRM PRIME pass-through, after Z29); own V4L2 M2M backend in StreamTransport.Linux with `V4L2_MEMORY_DMABUF` on both queues (stateful first: Qualcomm, MediaTek encode); stateless Request API only if a target board needs it; verify on the Qualcomm board, Rockchip needs a board |
| Z29 | Encoder input is chosen by a hard-coded switch: DMA-BUF goes to Vulkan import or VA-API mapping (`FFmpegVideoEncoder.cs:267-275`), so a backend taking DRM PRIME directly (rkmpp) has no path [V] | done: `GpuInput` per catalogue entry (D3D12 texture, D3D11 texture, Vulkan import, VA-API map, IOSurface); the encoder builds the declared input, a new way (DRM PRIME, V4L2 queues) is one enum member and one input [V Win, lab] |
| Z30 | `DmaBufImage.Device` is a `GpuIdentity`; on SoCs DMA-BUFs come from V4L2/MPP/ISP engines that are not GPUs, and a syncobj wait needs any DRM node, not the producer's [S] | todo with Z26: a media-device identity (DRM node, V4L2 node) or an optional allocator identity; decide when the V4L2 backend lands |
| Z27 | ECN on Windows. Earlier conclusion ("WSASendMsg with IP_ECN is rejected") came from sending CE, which Windows refuses by design (only the network marks CE); .NET 11 adds no managed control-message API [S, two external reviews 2026-10-06, Win32 winsock-ecn docs] | done: `EcnInterop.Windows.cs`: `IP_RECVECN`/`IPV6_RECVECN` + `WSARecvMsg` (extension pointer) to read `IP_ECN`/`IPV6_ECN`, `WSASendMsg` with an `IP_ECN` control message per datagram, `SIO_UDP_CONNRESET` off; Windows 10 build 20348+ (older keeps the managed socket, no ECN). `PrepareSend`/`Send` replace the Unix-only option call. ECT(0)/ECT(1) rows run on every OS, CE rows on Linux/macOS [V Win, Mac, lab] |
| Z28 | PipeWire.NET under the full lab suite: (a) `EachTriggeredPublish` lost its first trigger: a driver is made one before its stream starts, and a trigger in between runs no cycle; (b) the memory push test crashed 2/40 (SIGSEGV), symbolised with Arch's pipewire-debug 1.6.9: `do_trigger_driver` (stream.c:2732) ran from the data loop's invoke queue after the stream disconnected (process returned -EIO, node callbacks gone): `pw_stream_trigger_process` queues it without waiting and nothing flushes it on teardown; (c) `pw_stream_dequeue_buffer` pushes a busy buffer back onto the dequeued ring from the caller's thread, making that ring two-producer with the data loop [V lab] | done: `TriggerProcessAndWaitAsync` refuses followers at once and waits for Streaming before triggering; stream disposal drains the data loop's queued work (blocking empty invoke) before the disconnect, 0/80 crashes (was 10/80); producer dequeue/queue/return and capture requeues run under the data loop's lock. Upstream report candidates for the user: (b) trigger invoke not cancelled or flushed on disconnect, (c) busy push-back from a non-data thread |
| Z25 | V4L2 vivid tests are Inconclusive on the lab box (module not loaded) [V] | done: lab box loads `vivid n_devs=2 multiplanar=1,2` at boot with a udev rule for access; all 9 V4L2 tests run and pass |

Z13 design (2026-10-06). Fact [S, RADV 26.2.4 radv_device_memory.c:129, radv_amdgpu_bo.c:602]: memory
RADV allocates without the Mesa-private WSI allocate info is created `AMDGPU_GEM_CREATE_EXPLICIT_SYNC`,
and amdgpu command submission then syncs to the BO's kernel-move fences only. So fences attached to our
exported DMA-BUFs with `DMA_BUF_IOCTL_IMPORT_SYNC_FILE` are ignored by every amdgpu user (VA-API,
other RADV processes, radeonsi GL); implicit sync cannot carry our GPU work on AMD. Design:
- VulkanEngine submits without waiting: command-buffer ring on a timeline semaphore exported as a DRM
  syncobj; a completion thread runs deferred releases (descriptor sets, views, import leases, kept
  inputs) once their value is reached.
- Frames our Vulkan work produces carry `DmaBufImage.Sync` (`DrmSyncTimeline`: the engine's syncobj and
  the submit's point); release is by the frame's lease, so no release point.
- Readers wait where they run: our Vulkan batches wait GPU-side (timeline semaphore import; frames from
  implicit-sync producers through `DMA_BUF_IOCTL_EXPORT_SYNC_FILE` into a semaphore); the FFmpeg Vulkan
  encoder through the AVVkFrame's semaphore; PipeWire consumers through PipeWire explicit sync when
  they negotiate it. VA-API has no sync API: its input blocks on the syncobj point (a kernel wait, no
  polling), as do PipeWire consumers without explicit sync and CPU readbacks.

User directives this round: platform-specific tests use `[OSCondition]` (never runtime checks; MSTEST0061)
inside cross-platform test projects, and OS-specific test projects (1:1 with OS-specific source
projects) run only on their OS; hardware/environment absence stays Inconclusive with a reason. Every
repo: PipeWire.NET (65 files with runtime "PipeWire is a Linux daemon" guards; 6 classes mix managed and
daemon tests), Dtls.Core (Schannel, Network.framework, OpenSSL platform checks), the rest checked clean.
Push and update GH issues, descriptions and tags as work lands; no AI/session/ADR/historical text.

## Step 7a design: media mobility (T25 to T29), from precedent

Sources pulled into `docs/references/ice/`: libwebrtc `basic_ice_controller.cc`, `p2p_transport_channel.cc`,
`connection.cc`, `p2p_constants.h`, `ice_transport_internal.*`; RFC 8445, 7675, 8838, 8863, 9725 (WHIP),
draft-thatcher-ice-renomination.

- **Never clear the selection on a network event (T25).** libwebrtc keeps the selected pair and re-ranks;
  a switch needs the candidate pair writable. Ranking order (`CompareConnectionStates`/`CompareConnections`):
  writable, write state, receiving (with `receiving_switching_delay` 1 s against flapping), on the
  controlled side remote nomination then last data received, then network preference/cost and priority;
  RTT only with a minimum improvement. A network event re-checks every pair (fast pings) instead of
  discarding the selection.
- **Liveness from all received data (T26).** libwebrtc's pair is "receiving" if anything arrived within
  `kWeakConnectionReceiveTimeout` 2.5 s (configurable `receiving_timeout`), media and RTCP included; the
  selected pair is pinged at 480 ms when strong, 48 ms when weak; stable writable pairs every 2.5 s, others
  900 ms; unwritable after 5 failed pings in 5 s; dead at 30 s. For us: last-received per pair from the
  ICE data path, a configurable receiving timeout (IRL profile shorter), weak-state fast pings.
- **Controlled side follows data.** Without renomination the controlled agent prefers the nominated pair
  that last received data, which is how a controlling agent's switch reaches the far side. Evaluate
  draft-thatcher renomination (libwebrtc supports it) vs follow-the-data; RFC 8445 itself allows only an
  ICE restart.
- **Continual gathering (T27).** New interfaces get host candidates without a restart; trickled through
  the signaling channel (T29: WHIP/WHEP PATCH with `application/trickle-ice-sdpfrag`, RFC 9725 section 4.3)
  and checked against known remote candidates.
- **Congestion state per path (T28).** On a switch the controller restarts from the new pair's measured
  RTT (the keep-alive RTT already measured on standby pairs); path MTU state is per pair already (T12).

**Sources beyond libwebrtc (checked 2026-10-07).**
- **RFC 9000 section 9.4** (`docs/references/quic/rfc9000.txt`): on a new path reset the congestion controller
  and RTT estimator to initial values, except when only the port changed (NAT rebinding), where the state
  may be kept; packets from the old path must not feed the new path's estimates; probe loss is kept out
  of congestion control. This decides T28: reset on an address change, keep on a port-only change.
- **Multipath QUIC, draft-ietf-quic-multipath-21** (RFC Editor queue since 2026-09,
  `docs/references/quic/`): per-path congestion control and RTT state, path status available/backup,
  path abandon. The model for keeping a standby path's measurements warm and switching to them (T28),
  and for any later bonding.
- **Pion ICE** (second implementation): renomination with the NOMINATION attribute ("last nomination
  wins", strictly increasing 24-bit value; pion's default attribute is 0x0030, libwebrtc's 0xC001),
  continual gathering (GatherContinually), disconnected/failed timeouts from the last received packet,
  keepalive every 2 s. Renomination is an explicit API call there; libwebrtc increments it on every
  selected-pair switch. Ours: the controlled side follows nomination, then the newest data (libwebrtc's
  rule, works with any controlling peer); sending NOMINATION behind `a=ice-options:renomination` is a
  later item (both of our own ends already follow the data).
- **draft-thatcher-ice-renomination-01**: `ice-options:renomination`, NOMINATION attribute; aggressive
  nomination off when signalled.
- **Happy Eyeballs v3, draft-ietf-happy-happyeyeballs-v3-04** (July 2026): dual-stack racing and
  ordering, IPv6 first; with RFC 8421 (ICE dual-stack candidate priorities) the basis for IPv6-first pair
  ordering and for when an IPv4 alternate is tried.
- **PCP, RFC 6887** (and draft-penno-rtcweb-pcp): a host can open an inbound pinhole on a PCP-capable
  CPE firewall or NAT, which is exactly what a home IPv6 firewall needs for unsolicited checks from a
  moved field agent (T29). RFC 6092 CPE behaviour: outbound traffic opens state, so simultaneous checks
  from both sides (signaled candidates) punch through without PCP.
- **SCReAMv2, draft-ietf-ccwg-rfc8298bis-screamv2** (adopted by CCWG, May 2026): the controller's spec;
  per-path state follows RFC 9000 above.
- **RFC 9725 WHIP** section 4.3 trickle/restart via PATCH (`application/trickle-ice-sdpfrag`, RFC 8840) for T29;
  WHEP is draft-ietf-wish-whep.

**QUIC and HTTP/3 media (checked 2026-10-07, for T21; texts in `docs/references/quic/`).**
- **RFC 9221** QUIC DATAGRAM: unreliable frames inside the congestion-controlled, encrypted connection.
  .NET: System.Net.Quic has no datagram API yet (dotnet/runtime#123418 proposal; MsQuic itself supports
  it), so a QUIC media path in .NET needs either that API or MsQuic directly.
- **RTP over QUIC, draft-ietf-avtcore-rtp-over-quic-14** (+ draft-ietf-avtcore-sdp-roq): RTP/RTCP in
  QUIC streams or datagrams; congestion control is QUIC's, rate adaptation left to the application
  (section 5); connection migration pauses media for path validation, about one RTT (section 12.1).
- **Multipath QUIC, draft-ietf-quic-multipath-21** (RFC Editor queue): several paths at once, each with
  its own congestion control and RTT; the bonding answer for Wi-Fi plus cellular on a field device if
  StreamTransport ever carries media over QUIC.
- **QUIC NAT traversal, draft-seemann-quic-nat-traversal-02**: ICE over the same socket and then a QUIC
  connection on the nominated pair, or no ICE at all: ADD_ADDRESS / PUNCH_ME_NOW / REMOVE_ADDRESS frames
  over a proxied connection coordinate path validation to punch through, then migrate to the direct path.
  **QUIC address discovery, draft-ietf-quic-address-discovery-01**: OBSERVED_ADDRESS frames replace STUN
  server-reflexive discovery.
- **MASQUE CONNECT-UDP listen, draft-ietf-masque-connect-udp-listen-16** (submitted to IESG): a bound UDP
  proxy over HTTP/3 that can talk to many peers, made for WebRTC/ICE; a TURN replacement any HTTP/3 server
  (or a DevTunnel-style front) can host, and the proxied start for the NAT traversal draft above.
- **MoQ transport, draft-ietf-moq-transport-22**, over **WebTransport, draft-ietf-webtrans-http3-16** (WG
  last call): publish/subscribe media relays over QUIC/HTTP/3, the native HTTP/3 shape for one-to-many
  (StreamWeaver to viewers, relay fan-out). Not an RFC yet.
- Verdict for now: WebRTC/RTP over ICE stays the media path (browser and OBS interop, works today in
  .NET). The QUIC pieces worth building toward, in order: CONNECT-UDP listen as the relay/TURN
  alternative behind HTTP/3 (fits the IPv6-first, no-TURN-server stance), then RoQ/MoQ as a second
  transport adapter once .NET has QUIC datagrams; the transport-independent adaptation layer (ADR
  accepted 581c30a4) is what keeps that a second adapter instead of a rewrite.

## Latency and A/V sync: findings and tooling (2026-10-07)

**Finding (F17, open).** `Whip_PublishedTestSignal_ArrivesAndIsInSync` fails intermittently on the lab
box, also at 8b70516 (4 of 6), so it predates T12/RACK. Measured there (MeterListener over the library's
own instruments): Vulkan H.264 encode p50 3 ms / p95 7 ms, decode p50 1.5 ms / p95 4 ms; no UDP
RcvbufErrors (v4 or v6); no keyframe requests, skips or decode failures; yet ~40% of sent video frames
undelivered at the end and video latency growing to 3 s. Points at a sender queue held by a closed
congestion window. Lead: our SCReAM RTT sample subtracts the receiver's feedback hold, while SCReAMv2
(s_rtt per RFC 6298) and its reference C++ (`time_ntp - timeTx_ntp` of the newest packet in the report)
include it. Experiment running.

**Tooling (T30), after libwebrtc's video-timing extension** (`docs/references/timing/`):
- Sender stamps per timing frame, as 16-bit ms deltas from capture: encode start, encode finish,
  packetization done, pacer exit; two slots for relays. On the last packet of a frame only; a frame is a
  timing frame every 200 ms (`kDefaultTimingFramesDelayMs`) or when its size is an outlier.
- Receiver adds receive start/finish, assembled, decode start/finish, playout/render; with the sender
  clock offset (abs-capture-time's estimated capture clock offset, RTCP SR) it reports a full
  `TimingFrameInfo`-style breakdown across machines.
- Always-on, cheap: per-stage histograms (pacer queue, network, assembly, decode, playout delay) and a
  continuous A/V offset measure at playout (capture times of what is presented together), so the test
  signal is not the only way to see sync.
- Also evaluate the playout-delay extension (min/max playout delay from the sender) for low-latency
  intent.

**T31 (open, found 2026-10-07).** RTP header extensions are not negotiated: abs-capture-time goes out
under a hard-coded id 1 with no `a=extmap`, which RFC 8285 sections 5 and 7 forbid (ids are agreed in
SDP; the answer keeps the offerer's id per URI and drops what it does not understand; only extensions
the sender's own SDP mapped may be sent). Against a browser or OBS the id can name another extension.
Fix: an RFC 8285 extension map (one- and two-byte forms, `extmap-allow-mixed`), SDP negotiation, typed
extensions (abs-capture-time, video-timing, playout-delay), after libwebrtc's `RtpHeaderExtensionMap`.
It carries the T30 timing data.

**T30 done (2026-10-07).** Lip sync measured at presentation (3012f6a: `streamtransport.playout.av_offset`,
`MediaSessionStatistics.AvSyncOffset`, cross-checked against the test signal within 35 ms); per-frame
timing (video-timing extension, sender stamps + pacer exit stamped in place, receiver arrival/decode/
presentation, sender clock offset from SRs and the new per-pair ICE RTT; `IMediaSession.VideoFrameTimed`,
`streamtransport.video.timing.stage{stage}` and `.end_to_end`). T31 done (8b1f6d1).

**First measurements (Windows loopback, WHIP test signal, 640x360@30):** A/V sync 0.5 to 1.2 ms (session),
7 to 9 ms (test signal). Latency 512 ms end to end: encode queue 27, encode 69, packetize 0.2, pacer 0.2,
network 0.4, assembly 9.6, decode 0.0, playout 405 ms.

- **F18 (open, top priority).** The playout timeline treats video's systematic path delay as jitter (one
  offset, the fastest path over both streams, which is audio's) and keeps a leaky maximum that one startup
  spike sets: traced, jitter sat at 310 ms while frames ran 10 to 50 ms above the fastest path, decaying
  40 ms/s. Redesign after libwebrtc: a base delay per stream, jitter per stream from a robust windowed
  estimate that drops outliers and ignores startup, lip sync as the difference of the streams' bases, and
  the buffer shrinking at a bounded rate (libwebrtc's timing moves the current delay toward the target).
- **F19 (open).** The encoder holds each frame ~69 ms on Windows (encode call 3 ms): a pipelined
  encoder (async depth, lookahead or B-frames). Check each backend's low-delay settings.

**F18 done (b15092d, b573d0a).** Playout per stream (the faster stream's base is the offset, the slower
stream's base above it is lip sync), jitter as a windowed 95th percentile with outliers clamped, startup
streams kept out of the target, delay moving 100 ms/s. Windows loopback: video playout 405 -> 53 to 72 ms,
end to end 512 -> 94 to 124 ms with D3D12, A/V sync within 2 ms.

**F19 decided (1804a4a; later work in StreamTransport#26).** User decision 2026-10-07: rank encoders that change rate in place first now (after GPU input), and build native D3D12, Vulkan and VideoToolbox codec packages behind DI toward the end of StreamWeaver's development rather than patch FFmpeg. With NVENC chosen: latency 52 to 55 ms, end to end 50 to 55 ms on Windows loopback. Diagnosis: The modern-API encoders ranked first (h264_d3d12va on Windows,
h264_vulkan on Linux) cannot change rate while running in FFmpeg, so every congestion-control rate change
reopens the encoder: ~60 ms stall, forced IDR, frames dropped (Windows: encoder call p95 68 ms, 20 to 58 of
~150 frames dropped busy). Same GPU with NVENC (rate changes in place): latency 54 to 56 ms (test signal),
end to end 55 to 74 ms, call p95 1.8 ms, 3 to 6 drops, sync within 0.1 ms. FFmpeg's vulkan_encode builds
rate control per picture from bit_rate (init_pic_rc) but issues CmdControlVideoCodingKHR only once
(session_reset); the spec allows a later control with ENCODE_RATE_CONTROL_BIT alone, so runtime RC is a
small FFmpeg change. d3d12va likewise ignores the D3D12 rate-control reconfiguration support. We ship
BtbN's prebuilt FFmpeg 9, so the fix is upstream (user's call) or our own builds.
Remaining with NVENC: encode queue ~17 ms (capture to encoder start), encode stage 8 to 12 ms against
2.6 ms calls (some frames out a call late), assembly 7 to 10 ms. To investigate.

**Sister-library docs pass (2026-10-07).** Public docs brought up to the code: Dtls.Core f9e34b9 (connection
IDs, moving peers, record size limits, invariants), FFmpeg.Interop cf2f440 (explicit-sync import,
ExtraHardwareFrames), PipeWire.NET 05d3039 (stream time, colour, planar frames, held frames, per-format
modifier offers, host-memory fallback, pushed output), Syphon.NET 3c1c2ca (publish on a shared event),
Spout2.NET adfc7e7 (shader-writable shared texture). Changes still expected in sister libs: FFmpeg.Interop
for Z26 (rkmpp / V4L2 M2M on the field agent), Z19, Z9; PipeWire.NET possibly for Z23. Dtls.Core, Syphon.NET
and Spout2.NET have nothing open beyond optional extras (DTLS return routability checks).

**Latency, fully accounted (bc0b4e0, Windows loopback, NVENC, medians):** encode queue 0, encode 0 to 1,
packetize/pacer/network/receive under 1, assembly 0, decode 0.7, playout 46 to 49; end to end 49 to 51 ms,
test signal 50 to 52 ms, lip sync within 0.1 ms. The transport pipeline itself is 2 to 3 ms; the rest is
the playout buffer by policy (audio's 20 ms Opus frame + jitter + 20 ms margin, 40 ms floor). Fixed on the
way: decode finish taken at hand-over (hardware decoders deliver after Decode returns), receiver stamps on
one wall clock (an anchored mapping drifted by ms), the test-signal source stamping a schedule instead of
the capture moment (added ~16 ms and frame bursts). Stage summaries now report medians.


## State at handoff (2026-10-07)

Done today and pushed on `restructure`: 1804a4a encoder ranking, bc0b4e0 frame timing on one clock,
d9847da/bc6c746/fae5647 ICE mobility (T25-T27), 7a75175 congestion restart (T28), 932bb1a/07ea78d WHIP/WHEP
trickle and ICE restart (T29), 6fe0796 FlexFEC SDP negotiation (T14 part), d8879b8 alpha even width (A1), 4ec24db T15 recovery policy, a98d01f T32 repair over the second path, 01ee3c7 A2 alpha range, cdf2c5f A6 D3D12 NV12 in place, F23 FEC parity in place.
Windows: every suite passes. Mac: every suite passed at b038056 (T14 part and A1 included). Lab box unreachable since bc0b4e0 (asleep);
it has not run T25-T29, T14 or A1: run it first. Next: A2-A10, C1, T16, Z items.
