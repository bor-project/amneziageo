# Modified Xray runtime

This runtime uses Xray26.10.31, commit a4a3dff5b20c2faae567b742c172c7b95e75c083.
Upstream source: https://github.com/XTLS/Xray-core/tree/a4a3dff5b20c2faae567b742c172c7b95e75c083

The corresponding source changes are supplied in udp-worker-queue.patch. They set the inbound UDP pipe byte budget to256KiB and report discarded UDP packets through a bounded, rate-limited pressure reporter. Reporting records stage, counts, byte totals and queue capacity. It records no packet payload, packet hash, address or credential. Existing socket buffer and transport options are unchanged.

The complete eight-file patch, original/modified file checksums, source archive identity and Go1.27.3 compiler requirement are pinned in manifest.txt. The runtime cache identity is26.10.31-ag716.2. The complete standard-library build tool source accompanies this patch in the AmneziaGeo source distribution.

Xray and the modified Xray source use Mozilla Public License2.0. The upstream LICENSE is distributed beside this notice. Android also applies the source changes described in its Xray-source.md notice.
