# AEVRIX Publication Policy

AEVRIX is a public software project. Every publication must be sanitized before it reaches Git history.

## Mandatory pre-commit checks

Reject a commit or release if it contains:

- names or other personal identifiers not required by the software itself;
- personal e-mail addresses, phone numbers, addresses, identification numbers or personal documents;
- credentials, bearer tokens, OAuth material, cookies, session IDs or browser profiles;
- private keys, signing seeds, vaults, recovery codes, master passwords or bootstrap secrets;
- user-generated captures or reports that may contain PII;
- local absolute paths that reveal personal usernames or workstation identity;
- debug logs containing authentication headers or captured content.

## Allowed public material

- source code and tests using synthetic fixtures;
- architecture and protocol documentation;
- public product assets and branding;
- sanitized test evidence;
- hashes and metadata for historical artifacts;
- release binaries only after security and privacy review.

## Historical artifacts

If an original historical artifact contains a secret or personal data, the original must not be published merely for historical completeness. Publish a sanitized reconstruction or metadata record instead, clearly labeling it as such.

## Repository hygiene

`.gitignore`, CI secret scanning and release checks should be maintained as defense in depth. A secret once committed to public Git history must be considered exposed even if the file is later deleted.
