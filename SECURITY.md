# Security policy

## Supported versions

Security fixes go into a new release. Only the [latest release](https://github.com/Sigil-build/dictum/releases/latest) is supported, so please check that a problem is still there in it before you report it.

## Report a vulnerability

Please report it privately, not in a public issue: on the repository's [Security tab](https://github.com/Sigil-build/dictum/security), choose **Report a vulnerability**. GitHub then opens a private advisory that only you and the maintainers see.

Tell us:

- the Dictum version (`dictum --version`) and the system: Windows 11, or the Linux distribution with Wayland or X11;
- what an attacker could do, and what they need for it: local access, a crafted file, a server you connect;
- the steps to reproduce it, or a proof of concept.

Leave out API keys, recordings and anything else private.

Dictum's programs, its packages and the workflows that build and release them are in scope. A speech or LLM server you connect, and the Whisper models, belong to their own projects: report a problem in them there. That the packages are not code-signed yet is known; the next section shows how to check them meanwhile.

## What to expect

- A reply within a week, saying whether we can reproduce the problem.
- News in the advisory while we work on a fix.
- A release with the fix, then the published advisory, which credits you unless you would rather not be named.

## Verify a download

Dictum's packages are not code-signed yet, so Windows SmartScreen warns about them. These checks show that a file is the one this repository built and published.

### SHA256SUMS

Each release has `SHA256SUMS`, with the SHA-256 sum of each package. Download it to the folder with the package. On Linux, this prints `OK` after each file it finds:

```sh
sha256sum --check --ignore-missing SHA256SUMS
```

On Windows, in PowerShell, this prints `True` when the MSI matches:

```powershell
(Get-FileHash .\dictum-0.4.0-win-x64.msi).Hash -eq ((Select-String 'win-x64.msi' .\SHA256SUMS).Line -split ' ')[0]
```

The sums come from the same release as the files: they show that a download is complete and unchanged, not who built it. The attestation does that.

### Build attestation

For releases after 0.4.0. The release workflow signs an attestation for each file it builds, with a [Sigstore](https://www.sigstore.dev/) certificate that names the repository, the workflow, the tag and the commit. With the [GitHub CLI](https://cli.github.com/), signed in, in the folder with the file, for example:

```sh
gh attestation verify dictum-0.5.0-linux-x64.AppImage -R Sigil-build/dictum
```

It succeeds only for a file that a workflow of this repository built, and prints which workflow and which tag.

Each release also has `dictum-<version>-sbom.spdx.json`: the NuGet packages Dictum is built from, with their versions, in SPDX. It is attested for the packages, and `--predicate-type https://spdx.dev/Document/v2.3` added to the command above checks it.

### VirusTotal

The notes of each release have a VirusTotal section with a link for each file. [VirusTotal](https://www.virustotal.com/) scans a file with dozens of antivirus engines, and the report shows what each of them found. Dictum is not signed yet and is little known, so one or two engines may flag it as suspicious while the rest find nothing. If more of them flag a file, please report it as above.
