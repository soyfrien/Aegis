# Aegis

> **TODO:** One-sentence description of what Aegis is and who it is for.

This README is the single source of truth for understanding, setting up, and contributing to Aegis. If you read it top to bottom, you should be able to make your first contribution without asking anyone. If something here is unclear or out of date, that is a bug: please open an issue or a PR to fix it.

---

## Table of Contents

1. [Project Overview](#project-overview)
2. [Repository Structure](#repository-structure)
3. [Getting Started](#getting-started)
   - [Prerequisites](#prerequisites)
   - [Setup](#setup)
   - [Running the Project](#running-the-project)
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

> **TODO:** Fill in once the project's scope is defined.

- **What it does:** TODO
- **Why it exists / problem it solves:** TODO
- **Who uses it:** TODO
- **Current status:** Early setup. No application code has been committed yet.
- **Key concepts / glossary:** TODO (define any domain terms a newcomer will encounter).

---

## Repository Structure

> **TODO:** Replace this placeholder with the real layout as code is added. Keep it updated whenever top-level folders change.

```text
Aegis/
├── README.md        # This file: overview + contributing guide
├── TODO/            # Source code location (TBD)
├── TODO/            # Tests location (TBD)
└── TODO/            # Docs, scripts, config (TBD)
```

For each directory, add a one-line description of what belongs there and what does not.

---

## Getting Started

### Prerequisites

> **TODO:** List exact tools and minimum versions once the tech stack is chosen.

At minimum you will need:

- [Git](https://git-scm.com/) installed and configured with your name and email:
  ```bash
  git config --global user.name "Your Name"
  git config --global user.email "you@example.com"
  ```
- Access to this private repository (ask the maintainer to add you as a collaborator).
- An SSH key or personal access token set up with GitHub.
- TODO: language runtime / SDK and version
- TODO: package manager
- TODO: any other services (databases, containers, etc.)

### Setup

```bash
# 1. Clone the repository
git clone git@github.com:soyfrien/Aegis.git
cd Aegis

# 2. Install dependencies
# TODO: add the install command

# 3. Configure environment
# TODO: e.g. copy an example env file and fill in values.
# Never commit real secrets.
```

### Running the Project

```bash
# TODO: command to run locally
# TODO: command to run tests
# TODO: command to run the linter/formatter
```

If any step fails on a clean machine, please open an issue labeled `documentation`.

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

> **TODO:** Name the formatter and linter and link their configs once chosen.

General principles until tooling is in place:

- **Consistency wins:** match the style of surrounding code.
- **Automate it:** formatting should be done by a tool, not debated in review.
- Run the formatter and linter before every commit. TODO: commands.
- Consider pre-commit hooks so checks run automatically. TODO: setup instructions.
- Prefer small functions, descriptive names, and explicit error handling.
- Keep comments focused on *why*, not *what*.
- Use an `.editorconfig` (TODO) to standardize indentation and line endings.

---

## Testing Expectations

> **TODO:** Name the test framework and how to run it.

- Every bug fix includes a test that fails without the fix.
- Every new feature includes tests for its main paths and important edge cases.
- Tests must be deterministic: no reliance on network, time, or random ordering without control.
- Keep tests fast; mark slow or integration tests clearly. TODO: how.
- All tests must pass locally and in CI before merge.
- Coverage target: TODO (do not decrease coverage in a PR).

```bash
# TODO: run all tests
# TODO: run a single test file
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
6. TODO: any build/publish/deploy steps.

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

TODO: choose and add a license. Until then, this is a private repository and all rights are reserved by the owner.
