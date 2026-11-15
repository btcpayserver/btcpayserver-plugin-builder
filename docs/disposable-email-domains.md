# Disposable email domains

Registration rejects email addresses whose domain, or any parent domain, appears in `PluginBuilder/Data/disposable_email_blocklist.conf`. Matching is case-insensitive and uses the existing IDNA normalization in `PluginBuilder/Util/DisposableEmailDomains.cs`. The file is embedded in the application assembly and loaded once into the existing lookup; registration does not contact a detection service or download updates.

The embedded file contains the complete 10,181-domain snapshot from [disposable-email-domains/disposable-email-domains](https://github.com/disposable-email-domains/disposable-email-domains), revision [`ba992a0b4c867db3ff39605696bb7de9fbb2e11f`](https://github.com/disposable-email-domains/disposable-email-domains/tree/ba992a0b4c867db3ff39605696bb7de9fbb2e11f), dated November 8, 2026.

- Source file: [`disposable_email_blocklist.conf`](https://github.com/disposable-email-domains/disposable-email-domains/blob/ba992a0b4c867db3ff39605696bb7de9fbb2e11f/disposable_email_blocklist.conf).
- SHA-256: `8a303e28fe2704fc20e73bd320cb9f47e8b3658adc0ebb046260d5acf59c27c2`.
- Canonical format for that checksum: lowercase domains in sorted order, one per line, UTF-8, LF endings, including the final LF.
- License: CC0 1.0; the upstream [license text](licenses/disposable-email-domains-CC0.txt) is included.

To update, select and review a new upstream revision, replace `PluginBuilder/Data/disposable_email_blocklist.conf` with the upstream file, and update the revision, checksum and snapshot-integrity test together. Check additions and removals against the previous list before committing. Run the disposable-email domain and registration tests after the change. Updates to the embedded list require a new build and deployment.

The community list can be incomplete or become outdated as domains change ownership or service. A match is not evidence of account abuse, and an unlisted domain is not a guarantee of a permanent email address. This validation does not modify existing accounts.
