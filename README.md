# Aegis

> **Aegis** is a small, passcode-protected .NET 8 web app that wraps [yt-dlp](https://github.com/yt-dlp/yt-dlp) so you can paste a video link (YouTube, Facebook, …) into a mobile-friendly page, e.g. on an iPhone in Safari, and save the file to *Files* (or OneDrive).

This README is the single source of truth for understanding, setting up, and contributing to Aegis. If you read it top to bottom, you should be able to make your first contribution without asking anyone. If something here is unclear or out of date, that is a bug: please open an issue or a PR to fix it.

---

## Table of Contents

1. [Project Overview](#project-overview)
2. [Repository Structure](#repository-structure)
3. [Getting Started](#getting-started)
   - [Prerequisites](#prerequisites)
   - [Setup](#setup)
   - [Running the Project](#running-the-project)
   - [Deployment](#deployment)
   - [Share sheet, Home Screen app and Shortcuts (iPhone)](#share-sheet-home-screen-app-and-shortcuts-iphone)
   - [iPhone-compatible re-encoding](#iphone-compatible-re-encoding)
4. [How to Contribute (Quick Path)](#how-to-contribute-quick-path)
5. [Branching Strategy](#branching-strategy)
6. [Commit Message Conventions](#commit-message-conventions)
7. [Pull Request Process](#pull-request-process)
   - [PR Review Checklist](#pr-review-checklist)
8. [Code Style and Linting](#code-style-and-linting)
9. [Testing Expectations](#testing-expectations)
10. [Reporting Issues and Labels](#reporting-issues-and-labels)
11. [Documentation Expectations](#documentation-expectations)
12. [Reporting Security Issues](#reporting-security-issues)
13. [Versioning and Releases](#versioning-and-releases)
14. [Code of Conduct](#code-of-conduct)
15. [Getting Help](#getting-help)
16. [License](#license)

---

## Project Overview

- **What it does:** a single-page UI where you paste a URL and press **Download**. The server runs `yt-dlp` (+ `ffmpeg` for merging), shows job progress, then streams the finished MP4 back with `Content-Disposition: attachment` so iOS Safari offers *Save to Files*. An optional **Save to OneDrive** button uploads the same file through Microsoft Graph. A `/stats` page shows VM health and download statistics.
- **Why it exists / problem it solves:** iOS Safari cannot run yt-dlp; a tiny private server can.
- **Who uses it:** the owner and whoever they share the passcode with. It is *not* a public service.
- **Current status:** v0.1: working end to end; deployed on a single small Azure VM behind Caddy (automatic HTTPS).
- **Key concepts / glossary:**
  - **Job**: one download. Created by `POST /api/jobs`, polled via `GET /api/jobs/{id}`, fetched via `GET /api/jobs/{id}/file`.
  - **Passcode**: one shared secret from the `AEGIS_PASSCODE` environment variable; exchanging it at `/api/login` yields a cookie.
  - **TTL**: finished files are deleted from the server after `Aegis:FileTtlMinutes` (default 15) or on `DELETE /api/jobs/{id}`.

---

## Repository Structure

```text
Aegis/
├── README.md                    # This file: overview + contributing guide
├── Aegis.sln
├── src/Aegis.Web/               # ASP.NET Core (minimal API) app
│   ├── Program.cs               # wiring: auth, rate limiting, endpoints
│   ├── Services/                # yt-dlp runner, job manager, OneDrive client, stats collector, validators
│   └── wwwroot/                 # static UI: index.html, stats.html, shortcut.html, app.js, sw.js, manifest.webmanifest, icons/
├── tools/make_icons.py          # regenerates the PWA icons (needs Pillow)
├── tests/Aegis.Tests/           # xUnit tests (unit + in-memory API tests)
└── deploy/                      # systemd units, Caddyfile, install script, env example (no secrets)
```

Source code goes in `src/`, tests in `tests/`, and anything about running on a server in `deploy/`.

---

## Getting Started

### Prerequisites

**Stack:** C# / .NET 8 (ASP.NET Core minimal APIs), xUnit, vanilla HTML/CSS/JS front end (no build step), yt-dlp + ffmpeg, Caddy + systemd for deployment.

You will need:

- [Git](https://git-scm.com/) installed and configured with your name and email:
  ```bash
  git config --global user.name "Your Name"
  git config --global user.email "you@example.com"
  ```
- The repository is public: `git clone https://github.com/soyfrien/Aegis.git`.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.x)
- NuGet (bundled with the SDK); no other package manager
- [`yt-dlp`](https://github.com/yt-dlp/yt-dlp#installation) and [`ffmpeg`](https://ffmpeg.org/) on `PATH` (only needed to actually download; tests do not need them)
- No database: job state is in memory and files are temporary

### Setup

```bash
# 1. Clone the repository
git clone https://github.com/soyfrien/Aegis.git
cd Aegis

# 2. Install dependencies
dotnet restore

# 3. Configure environment (never commit real secrets)
export AEGIS_API_TOKEN="$(head -c 32 /dev/urandom | base64 | tr -d '/+=' | head -c 40)"   # optional, see "Optional headless mode"
export AEGIS_PASSCODE="$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | head -c 24)"   # shared passcode; remember it
```

Configuration is read from `appsettings.json` (section `Aegis`) and can be overridden with environment variables using `__` as separator, e.g. `Aegis__YtDlpPath=/usr/local/bin/yt-dlp`.

| Setting | Default | Meaning |
|---|---|---|
| `AEGIS_PASSCODE` | *(none, required)* | Shared passcode. If unset, nobody can log in. |
| `AEGIS_API_TOKEN` | *(none, optional)* | Long random token (24+ chars) that lets a Shortcut call `/api/jobs` and `/api/fetch` with the `X-Aegis-Token` header. Disabled when unset. |
| `Aegis:YtDlpPath` | `yt-dlp` | yt-dlp binary |
| `Aegis:FfmpegPath` | *(PATH)* | optional ffmpeg location |
| `Aegis:WorkDir` / `StateDir` | temp dir / `~/.local/share/aegis` | temp downloads / data-protection keys + OneDrive token |
| `Aegis:CookiesFile` | *(none)* | optional Netscape `cookies.txt` exported from your own browser, passed to yt-dlp (`--cookies`) |
| `Aegis:MaxConcurrentJobs` | 2 | parallel yt-dlp processes |
| `Aegis:MaxFileSize` | `1G` | passed to `--max-filesize` |
| `Aegis:MaxHeight` | 720 | preferred max video height |
| `Aegis:Reencode:TimeoutMinutes` | 30 | give up re-encoding after this long and keep the original file |
| `Aegis:Reencode:Preset` | `superfast` | libx264 preset for full re-encodes (`ultrafast`…`veryslow`). Faster = quicker but bigger files at the same quality (crf 23); unknown values fall back to `superfast` |
| `Aegis:Reencode:Nice` | 10 | Linux niceness of ffmpeg (0 = off) so encodes don't starve the web server |
| `Aegis:FileTtlMinutes` | 15 | how long a finished file stays on the server |
| `Aegis:LoginAttemptsPerMinute` / `RequestsPerMinute` | 5 / 60 | per-IP rate limits |
| `Aegis:OneDrive:ClientId` / `TenantId` / `Folder` | *(empty)* / `consumers` / `Aegis` | optional OneDrive feature |

#### Optional: "Save to OneDrive"

Without configuration the button just explains that OneDrive is not set up. To enable it, register a **public-client** app in Microsoft Entra ID and set its client id:

```bash
az ad app create --display-name Aegis --sign-in-audience AzureADandPersonalMicrosoftAccount \
  --is-fallback-public-client true --public-client-redirect-uris http://localhost
# add the delegated Microsoft Graph permission Files.ReadWrite (and offline_access) in the portal if prompted, then:
export Aegis__OneDrive__ClientId=<appId from the command above>
```

Aegis uses the OAuth **device-code flow**: tap *Save to OneDrive*, open the shown Microsoft link, enter the code, then tap the button again. The refresh token is stored encrypted (ASP.NET data protection) in `Aegis:StateDir`. Files go to the `Aegis` folder in your OneDrive using a resumable Graph upload.

### Running the Project

```bash
# run locally (http://localhost:5000)
export AEGIS_PASSCODE=...        # see Setup
dotnet run --project src/Aegis.Web --urls http://localhost:5000

# run tests
dotnet test

# format check (the .NET SDK formatter)
dotnet format --verify-no-changes
```

Open the page, enter the passcode, paste a URL, press **Download**, then **Save to Files**. Server stats live at `/stats` (JSON: `/api/stats`).

Quick check with curl:

```bash
curl -c jar -H 'Content-Type: application/json' -d "{\"passcode\":\"$AEGIS_PASSCODE\"}" http://localhost:5000/api/login
curl -b jar -H 'Content-Type: application/json' -d '{"url":"https://www.youtube.com/watch?v=jNQXAC9IVRw"}' http://localhost:5000/api/jobs
curl -b jar http://localhost:5000/api/jobs/<id>                       # poll until "state":"done"
curl -b jar -OJ http://localhost:5000/api/jobs/<id>/file               # saves with the server's filename
```

### Deployment

Aegis runs as a systemd service (`deploy/aegis.service`, user `aegis-app`) listening on `127.0.0.1:5000`, behind **Caddy** (`deploy/Caddyfile`) which terminates HTTPS with an automatic Let's Encrypt certificate. The reference deployment is an Azure B1s-class VM (1 GB RAM), so **publish on a bigger machine and copy the result** rather than building on the VM:

```bash
dotnet publish src/Aegis.Web -c Release -o publish --no-self-contained
tar -czf aegis-publish.tar.gz -C publish .
# copy aegis-publish.tar.gz and the deploy/ folder to the VM, then on the VM (as root):
sudo mkdir -p /etc/aegis && sudoedit /etc/aegis/aegis.env     # see deploy/aegis.env.example; contains AEGIS_PASSCODE
sudo bash deploy/install.sh /tmp/aegis-publish.tar.gz
```

The VM needs `aspnetcore-runtime-8.0`, `ffmpeg`, `yt-dlp` (zipapp/standalone at `/usr/local/bin/yt-dlp`), `deno` (JS runtime for YouTube) and `caddy`; ports 80/443 open. `deploy/install.sh` also installs a **systemd timer** (`aegis-ytdlp-update.timer`, daily) that runs `yt-dlp -U`, because sites break old yt-dlp versions constantly.

Notes:
- The passcode lives only in `/etc/aegis/aegis.env` on the server (mode 640). Rotate by editing the file and `systemctl restart aegis`.
- YouTube blocks most datacenter IPs (including Azure's) with a "Sign in to confirm you're not a bot" challenge. Updating yt-dlp, installing a JavaScript runtime ([Deno](https://deno.com), needed by current yt-dlp for YouTube) and trying other player clients did **not** get around it from the reference VM. What can work: export cookies from a browser where you are logged in to YouTube (Netscape `cookies.txt`), put the file on the server (e.g. `/var/lib/aegis/state/cookies.txt`, readable only by `aegis-app`) and set `Aegis__CookiesFile` to it. Be aware that automated use of an account can get that account flagged. Other sites (Vimeo, direct video links, many others) work without this.
- Only download content you have the right to download.

If any step fails on a clean machine, please open an issue labeled `documentation`.

### Share sheet, Home Screen app and Shortcuts (iPhone)

Some iPhone apps give you a *Share* button but no way to copy a link. Aegis handles this in three layers:

1. **Home Screen app (PWA).** Aegis ships a web app manifest (`/manifest.webmanifest`), icons (`/icons/`, regenerate with `python3 tools/make_icons.py`), Apple meta tags and a tiny service worker (`/sw.js`, only caches the page shell; it never touches `/api`). In Safari: *Share → Add to Home Screen*. Note that iOS keeps Safari and Home Screen apps logged in separately, so you unlock Aegis once in each.
2. **`/share` landing route.** iOS Safari does **not** support the Web Share Target API for installed web apps (Chrome on Android does, and the manifest declares a `share_target` for it). Instead, anything can open `https://<host>/share?url=…&text=…&title=…` (or `/?url=…`). The server finds the **first http(s) link** in `url`, then `text`, then `title` (so "Look at this https://… wow" works) and redirects to `/?target=<link>&auto=1`. The page keeps the target through the login form, pre-fills the box and starts the download. Add `&auto=0` to only pre-fill. No link found → `/?shared=none` and a friendly message. The route never needs a login itself: the download is still protected by the normal auth.
3. **iOS Shortcut.** A Shortcut with *Show in Share Sheet* turned on puts "Aegis" in every app's Share Sheet. The logged-in page **`/shortcut`** has step-by-step instructions (the address is filled in for you). In short: *Receive URLs and Text from Share Sheet → URL Encode → Text `https://<host>/share?url=` + the encoded text → Open URLs*.

A `.shortcut` file is not shipped: iOS only imports Shortcuts that Apple has signed (signing requires macOS), and a prebuilt file would also have to embed the server address and, for the headless variant, a secret.

#### Optional headless mode (API token)

Set `AEGIS_API_TOKEN` (e.g. `head -c 32 /dev/urandom | base64 | tr -d '/+=' | head -c 40`) in `/etc/aegis/aegis.env` and restart. A client may then send the header `X-Aegis-Token: <token>` instead of the login cookie, **only** to:

| Endpoint | Purpose |
|---|---|
| `POST /api/fetch` `{"url": "…"}` | Waits for the download and returns the video file itself (`Content-Disposition: attachment`). Ideal for a Shortcut: *Get Contents of URL → Save File*. |
| `POST /api/jobs`, `GET /api/jobs/{id}`, `GET /api/jobs/{id}/file`, `DELETE /api/jobs/{id}` | The normal job API. |

The token cannot reach OneDrive, stats or any other endpoint. `url` may also be pasted text; the first link in it is used. Keep the token out of git and chat; anyone holding it can download through your server, so rotate it by editing the env file and restarting.

```bash
curl -H "X-Aegis-Token: $AEGIS_API_TOKEN" -H 'Content-Type: application/json' \
     -d '{"url":"https://example.com/video.mp4"}' -OJ https://<host>/api/fetch
```

### iPhone-compatible re-encoding

Some downloads (VP9, AV1, WebM, MKV, 10-bit H.264 …) will not play on an iPhone. The main page has a **Make iPhone-compatible** setting, remembered per device in `localStorage` (so it also applies when a download is started from the share sheet):

| Mode | API value | What happens |
|---|---|---|
| Off (default) | `off` | The file is delivered as downloaded. |
| Auto | `auto` | The file is probed with `ffprobe`. If it is already H.264 or HEVC video with AAC audio (or no audio) in an MP4/MOV, nothing is done. Otherwise the **cheapest fix** is used: just repackage into MP4 when only the container is wrong (seconds, also fixes HEVC `hev1` → `hvc1`), copy the video and encode only the audio when the video is fine, or a full re-encode. |
| Always | `always` | Always re-encode to H.264/AAC MP4. |

Full re-encodes use `ffmpeg` with `libx264` (`-preset superfast -crf 23 -pix_fmt yuv420p -profile:v high`), AAC 160 kbps stereo and `-movflags +faststart`. Video is capped at **1080p** (never upscaled; either orientation). The preset is configurable with `Aegis:Reencode:Preset` (default `superfast`: fast on a small VM, files somewhat larger than with `veryfast`). The job shows *Re-encoding… NN%* (parsed from `ffmpeg -progress`).

Safeguards for a small VM: only **one encode runs at a time** (others wait, without blocking downloads), ffmpeg runs under `nice`, there is a timeout (`Aegis:Reencode:TimeoutMinutes`), and temp files are removed. If conversion fails or times out, you still get the **original file** and the job carries a clear `notice`. Re-encoding is slow on a 2 vCPU burstable VM: expect several minutes for a short clip, and longer than the video for long ones.

Programmatic use: `POST /api/jobs` and `POST /api/fetch` accept an optional `"reencode": "off" | "auto" | "always"` (also with `X-Aegis-Token`); an unknown value returns 400. The job JSON includes `reencode` (requested mode), `reencodeResult` (`none`, `skipped`, `remuxed`, `audio`, `reencoded`, `failed`) and `notice`. `/stats` shows how many re-encodes ran, failed and how long they took.

```bash
curl -H "X-Aegis-Token: $AEGIS_API_TOKEN" -H 'Content-Type: application/json' \
     -d '{"url":"https://example.com/video.webm","reencode":"auto"}' -OJ https://<host>/api/fetch
```

---

## How to Contribute (Quick Path)

1. **Find or open an issue** describing the work. For anything non-trivial, agree on the approach in the issue before writing code.
2. **Sync `main`:** `git checkout main && git pull`.
3. **Create a branch** using the [naming rules](#branching-strategy).
4. **Make small, focused commits** following [Conventional Commits](#commit-message-conventions).
5. **Run tests and linters locally** before pushing.
6. **Push and open a Pull Request** against `main`, filling out the description fully.
7. **Address review feedback**, then a maintainer merges.
8. **Delete your branch** after merge.

---

## Branching Strategy

We use a simple trunk-based flow:

- **`main`** is always releasable. Never commit directly to `main`; all changes go through a PR.
- Create short-lived branches off `main` and merge them back quickly (ideally within a few days).

### Branch naming

```text
<type>/<issue-number>-<short-description>
```

| Type       | Use for                                  | Example                          |
|------------|------------------------------------------|----------------------------------|
| `feat`     | New features                             | `feat/12-user-login`             |
| `fix`      | Bug fixes                                | `fix/34-null-pointer-on-save`    |
| `docs`     | Documentation only                       | `docs/5-setup-instructions`      |
| `refactor` | Code changes without behavior change     | `refactor/40-split-config`       |
| `test`     | Adding or fixing tests                   | `test/41-auth-edge-cases`        |
| `chore`    | Tooling, dependencies, maintenance       | `chore/50-update-deps`           |
| `hotfix`   | Urgent production fixes                  | `hotfix/60-crash-on-startup`     |

Rules:
- Use lowercase and hyphens; no spaces.
- Keep your branch up to date by rebasing on `main` (`git fetch && git rebase origin/main`) rather than merging `main` into it.
- One logical change per branch.

---

## Commit Message Conventions

We follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/). This keeps history readable and enables automated changelogs and version bumps.

### Format

```text
<type>(<optional scope>): <short summary>

<optional body: what and why, wrapped at ~72 chars>

<optional footer(s): BREAKING CHANGE: ..., Closes #123>
```

### Types

- `feat`: a new feature (triggers a MINOR version bump)
- `fix`: a bug fix (triggers a PATCH bump)
- `docs`: documentation only
- `style`: formatting, whitespace; no code meaning change
- `refactor`: code change that neither fixes a bug nor adds a feature
- `perf`: performance improvement
- `test`: adding or correcting tests
- `build`: build system or dependencies
- `ci`: CI configuration
- `chore`: other maintenance
- `revert`: reverts a previous commit

### Rules

- Summary in the imperative mood: "add", not "added" or "adds".
- Summary ≤ 72 characters, lowercase start, no trailing period.
- Explain *why* in the body when it is not obvious.
- Mark breaking changes with `!` after the type (`feat!: ...`) **and/or** a `BREAKING CHANGE:` footer (triggers a MAJOR bump).
- Reference issues in the footer: `Closes #42`.

### Examples

```text
feat(auth): add password reset flow

fix: handle empty config file without crashing

Closes #17

feat(api)!: rename /users endpoint to /accounts

BREAKING CHANGE: clients must update the endpoint path.
```

---

## Pull Request Process

1. **Open the PR against `main`.** Use a Draft PR if the work is not ready for review.
2. **Title** follows Conventional Commits format (it may become the squash-merge commit message).
3. **Description** must include:
   - **What** changed and **why**.
   - Linked issue (`Closes #123`).
   - **How to test** it, step by step.
   - Screenshots or logs for visible/behavioral changes.
   - Any risks, follow-ups, or breaking changes.
4. **Keep PRs small.** Aim for changes a reviewer can understand in under ~30 minutes. Split large work into a series of PRs.
5. **Checks must pass** (tests, lint, build) before review. TODO: list CI checks once configured.
6. **Reviews:** at least one approving review from a maintainer is required. TODO: adjust once branch protection is configured.
7. **Responding to feedback:** reply to every comment, push fixes as new commits during review (easier to re-review), and re-request review when ready. Resolve a thread only once it's addressed.
8. **Merging:** maintainers merge using **squash and merge** so each PR becomes one clean commit on `main`. Delete the branch afterward.

### PR Review Checklist

Authors should self-review against this list before requesting review; reviewers use it too.

- [ ] The PR addresses a single, clearly described concern.
- [ ] Linked to an issue and the description explains what and why.
- [ ] Code is readable, with clear names and no dead or commented-out code.
- [ ] Follows the project style; linters and formatters pass.
- [ ] New or changed behavior is covered by tests; all tests pass.
- [ ] Edge cases and error handling are considered.
- [ ] No secrets, credentials, or personal data committed.
- [ ] No unnecessary new dependencies; any new ones are justified.
- [ ] Documentation (README, comments, docs) is updated where relevant.
- [ ] Breaking changes are clearly flagged.
- [ ] Commit messages follow Conventional Commits.

**Reviewer etiquette:** be kind and specific, explain the reasoning behind requests, and distinguish blocking issues from suggestions (prefix optional ones with `nit:`). Aim to respond to review requests within 2 business days.

---

## Code Style and Linting

Formatting is enforced with the .NET SDK's `dotnet format` (rules from `.editorconfig` defaults).

General principles until tooling is in place:

- **Consistency wins:** match the style of surrounding code.
- **Automate it:** formatting should be done by a tool, not debated in review.
- Run `dotnet format` before every commit.
- Consider pre-commit hooks so checks run automatically. TODO: setup instructions.
- Prefer small functions, descriptive names, and explicit error handling.
- Keep comments focused on *why*, not *what*.
- Use an `.editorconfig` (TODO) to standardize indentation and line endings.

---

## Testing Expectations

Tests use **xUnit** (`tests/Aegis.Tests`), including in-memory API tests via `WebApplicationFactory`. They never call the network or yt-dlp (a fake downloader is injected).

- Every bug fix includes a test that fails without the fix.
- Every new feature includes tests for its main paths and important edge cases.
- Tests must be deterministic: no reliance on network, time, or random ordering without control.
- Keep tests fast; mark slow or integration tests clearly. TODO: how.
- All tests must pass locally and in CI before merge.
- Coverage target: TODO (do not decrease coverage in a PR).

```bash
dotnet test                                        # all tests
dotnet test --filter FullyQualifiedName~ApiTests   # one test class
```

---

## Reporting Issues and Labels

### Before opening an issue

- Search existing issues (open and closed) to avoid duplicates.
- For security problems, **do not** open a public issue; see [Reporting Security Issues](#reporting-security-issues).

### Bug reports should include

- A clear, descriptive title.
- Steps to reproduce (minimal and exact).
- Expected vs. actual behavior.
- Environment details (OS, versions, configuration).
- Logs, error messages, or screenshots.

### Feature requests should include

- The problem you are trying to solve (not just the solution).
- Proposed solution and alternatives considered.
- Any impact on existing behavior.

### Labels

| Label              | Meaning                                         |
|--------------------|-------------------------------------------------|
| `bug`              | Something isn't working                         |
| `enhancement`      | New feature or improvement                      |
| `documentation`    | Docs additions or fixes                         |
| `question`         | Needs clarification or discussion               |
| `good first issue` | Well-scoped, good for newcomers                 |
| `help wanted`      | Maintainers would welcome contributions         |
| `priority: high`   | Should be addressed soon                        |
| `blocked`          | Waiting on something else                       |
| `wontfix`          | Will not be worked on (with explanation)        |
| `duplicate`        | Already tracked elsewhere (link it)             |

TODO: create these labels in the repository settings.

---

## Documentation Expectations

- Update this README when you change setup steps, structure, commands, or workflow.
- Public functions, modules, and APIs should have doc comments explaining purpose, inputs, outputs, and errors.
- Record significant design decisions (TODO: e.g. in a `docs/decisions/` folder) with context, the decision, and its consequences.
- Keep examples runnable and up to date.
- User-facing changes should be noted in the changelog (see [Versioning and Releases](#versioning-and-releases)).

---

## Reporting Security Issues

**Please do not report security vulnerabilities through public issues, PRs, or discussions.**

- Report privately via GitHub's **Security → Report a vulnerability** (private vulnerability reporting), or contact the maintainer directly. TODO: add a security contact.
- Include a description, steps to reproduce, affected versions, and potential impact.
- Expect an acknowledgment within TODO days. Please allow time for a fix before any public disclosure.
- Never commit secrets. If you accidentally do, tell a maintainer immediately so the credential can be rotated; deleting the commit is not enough.

TODO: add a `SECURITY.md` with the full policy.

---

## Versioning and Releases

We use [Semantic Versioning](https://semver.org/) — `MAJOR.MINOR.PATCH`:

- **MAJOR**: incompatible / breaking changes
- **MINOR**: backward-compatible new features
- **PATCH**: backward-compatible bug fixes

Pre-1.0 (`0.y.z`) versions may change more freely while the project stabilizes.

### Release process

1. Ensure `main` is green (all checks passing).
2. Determine the next version from commits since the last release (Conventional Commits make this mechanical).
3. Update the changelog (TODO: `CHANGELOG.md`, following [Keep a Changelog](https://keepachangelog.com/)).
4. Tag the release: `git tag -a vX.Y.Z -m "vX.Y.Z" && git push origin vX.Y.Z`.
5. Publish a GitHub Release with the changelog notes.
6. Publish and deploy as described in [Deployment](#deployment).

---

## Code of Conduct

We want this to be a welcoming, respectful project for everyone. In short:

- Be respectful, patient, and constructive.
- Critique ideas and code, never people.
- Assume good intent and ask clarifying questions.
- No harassment, discrimination, or personal attacks of any kind.

Unacceptable behavior may be reported to the maintainer (TODO: contact) and can result in removal from the project. TODO: adopt a full `CODE_OF_CONDUCT.md` (e.g. the [Contributor Covenant](https://www.contributor-covenant.org/)).

---

## Getting Help

- **Questions about the code or setup:** open an issue labeled `question`.
- **Stuck on a PR:** comment on the PR and mention a maintainer.
- **Maintainer:** [@soyfrien](https://github.com/soyfrien)
- TODO: add any chat channel or discussion forum.

When asking for help, include what you tried, what you expected, and what happened.

---

## License

TODO: choose and add a license. Until then, all rights are reserved by the owner.
