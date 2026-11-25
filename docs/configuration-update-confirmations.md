# Configuration update confirmations

Background subscription checks leave changed configuration files untouched and record a pending server revision with the revision of the current local file. The application sends both revisions when the user confirms an update. A delayed confirmation cannot overwrite a file edited after the confirmation was displayed.

When the local file changes, the first stale confirmation preserves it and refreshes the pending offer. A subsequent confirmation uses the new local revision. Older two-argument IPC requests remain accepted and capture the pending base when the request enters the service; current applications always send the explicit base revision.

A hello revision matching the local file clears an obsolete pending offer without downloading the subscription. A matching pending revision avoids downloading only while its local base still matches. Older servers without a per-file revision retain the subscription check path.

A successful subscription read removes nodes missing from a nonempty subscription, clears their pending updates and marks their membership absent. The agent receives the removed names and stops the affected tunnel through its existing controller. DNS and transport settings remain available if the node returns. New nodes are added by the existing manual refresh operation.

Failed HTTP/TLS reads and empty subscriptions do not remove configurations or pending offers. Manual refresh retains the existing bulk-update behavior.
