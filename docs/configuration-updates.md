# Configuration updates

Scheduled subscription checks discover changes without replacing existing configuration files. A pending update records its revision, the revision of the local file and changed field names. It does not contain a second copy of the private configuration.

The application offers Update or Later for a new configuration revision. Dismissing the offer remembers that revision. The update remains available in the configuration settings, including after restarting the application. An open modal or unsaved configuration edits postpone the offer.

Update in a configuration's settings applies only that configuration. It fetches the subscription again and verifies both the confirmed server revision and the local file revision. A newer server revision requires confirmation again. Failed downloads, certificate failures, invalid files and changed local files preserve the current configuration. Applying to an active session uses the platform's existing reconnect flow. User routing, proxy and DNS preferences stored beside the raw file remain in place.

The complete raw configuration participates in the revision, including headers, signature packets, padding and handshake/rekey settings. A changed future field is detected without a list of known keys. Cosmetic line-ending, key-case and separator formatting follow existing subscription comparison rules.

The optional per-configuration revision in hello detects an outdated imported file at the first server interaction. Servers without it use the existing subscription revision and scheduled checks. Explicit refresh commands retain their existing bulk-apply behaviour. Apple is outside this iteration.
