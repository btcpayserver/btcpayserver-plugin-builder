# Disposable email domains

Registration rejects email addresses whose domain, or any parent domain, appears in `PluginBuilder/Data/disposable_email_blocklist.conf`. Matching is case-insensitive and uses the existing IDNA normalization in `PluginBuilder/Util/DisposableEmailDomains.cs`. The file is embedded in the application assembly and loaded once into the existing lookup; registration does not contact a detection service or download updates.

The embedded file contains 10,171 domains, based on the 10,181-domain snapshot from [disposable-email-domains/disposable-email-domains](https://github.com/disposable-email-domains/disposable-email-domains), revision [`ba992a0b4c867db3ff39605696bb7de9fbb2e11f`](https://github.com/disposable-email-domains/disposable-email-domains/tree/ba992a0b4c867db3ff39605696bb7de9fbb2e11f), dated November 8, 2026, with the local corrections below.

- Source file: [`disposable_email_blocklist.conf`](https://github.com/disposable-email-domains/disposable-email-domains/blob/ba992a0b4c867db3ff39605696bb7de9fbb2e11f/disposable_email_blocklist.conf).
- Upstream snapshot SHA-256: `8a303e28fe2704fc20e73bd320cb9f47e8b3658adc0ebb046260d5acf59c27c2`.
- Embedded list SHA-256 after local corrections: `96eed897524f3850f9f7d67e1833abf1fbe59d9294e860791d5974906adab360`.
- Canonical format for that checksum: lowercase domains in sorted order, one per line, UTF-8, LF endings, including the final LF.
- License: CC0 1.0; the upstream [license text](licenses/disposable-email-domains-CC0.txt) is included.

Local corrections reviewed on November 15, 2026 remove these 11 false positives. The linked operator pages publish permanent accounts or personal, business or institutional contacts in the exact domain:

| Removed domain | Operator evidence |
| --- | --- |
| `21cn.com` | [Permanent mailbox registration](https://e.189.cn/register/21cn_email/step1.do) |
| `5july.org` | [Foundation contact](https://5july.org/) |
| `atomauto.vn` | [Automotive business contact](https://atomauto.vn/gioi-thieu) |
| `dhm.ro` | [Advertising agency contact](https://www.dhm.ro/contact) |
| `digitalsanctuary.com` | [Personal and professional contact](https://digitalsanctuary.com/pages/about) |
| `hola.org` | [Corporate privacy contact](https://hola.org/legal/privacy) |
| `jsrsolutions.com` | [Software company contacts](https://www.jsrsolutions.com/) |
| `lukecarriere.com` | [Professional portfolio contact](https://lukecarriere.com/) |
| `neomailbox.com` | [Mail operator's corporate contacts](https://secure.neomailbox.com/contact/) |
| `privacy.net` | [Editorial site's privacy contact](https://privacy.net/privacy-policy/) |
| `stanfordujjain.com` | [School contacts](https://stanfordujjain.com/) |

The removed `5july.org` entry is replaced with `flashbox.5july.org`. The operator identifies that subdomain as its [public disposable inbox service](https://flashbox.5july.org/en/how-it-works), while using the parent domain for the foundation's contact. Flashbox and all its subdomains remain blocked.

To update, select and review a new upstream revision, import its file into `PluginBuilder/Data/disposable_email_blocklist.conf`, and reapply the local corrections above. Keep the domains sorted and update the source revision, domain count, upstream checksum, embedded-list checksum and integrity test together. Check additions and removals against the previous list before committing. Run the disposable-email domain and registration tests, including the false-positive and Flashbox regressions. Updates to the embedded list require a new build and deployment.

The community list can be incomplete or become outdated as domains change ownership or service. A match is not evidence of account abuse, and an unlisted domain is not a guarantee of a permanent email address. This validation does not modify existing accounts.
