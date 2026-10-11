# Plan: GitHub Actions CI (PR build+test) and Docker Hub publish for joeldroid/jnotes

## Goal
Add GitHub Actions so every pull request to `main` builds the solution and runs the full test suite, including the PostgreSQL integration tests, with no secrets needed and nothing pushed. Pushes to `main`, `v*` tags and manual runs repeat the same build and test. Only if that passes do they build the Docker image, smoke-check it, and push it to Docker Hub `joeldroid/jnotes`. The Docker Hub credentials come from repository settings and are never written into any file.

## Context found
- **No `.github/` directory exists yet.** Every file below is new except `README.md`. The remote is `https://github.com/joeldroid/JNotes.git`, so badge URLs use `joeldroid/JNotes`.
- `JNotes.slnx` has three projects: `src/JNotes.Core`, `src/JNotes.Web`, `tests/JNotes.Tests`.
- `global.json` pins SDK `10.0.100` with `rollForward: latestMinor`. So `actions/setup-dotnet` with `dotnet-version: 10.0.x` is allowed. Don't use `global-json-file`, which would install exactly the old 10.0.100.
- `Directory.Build.props` sets `EnforceCodeStyleInBuild=true`. There is no `Directory.Packages.props`.
- **There are no `packages.lock.json` files**, so `setup-dotnet`'s built-in `cache: true` is not usable. Use `actions/cache` keyed on the `*.csproj` files, `Directory.Build.props` and `global.json`.
- `.config/dotnet-tools.json` only contains `dotnet-ef`, which CI doesn't need. No `dotnet tool restore`.
- Tests (`tests/JNotes.Tests/Infrastructure/TestPostgresDatabase.cs`):
  - They read `JNOTES_TEST_POSTGRES_CONNECTION_STRING`, defaulting to `Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes`.
  - Each test class runs CREATE/DROP DATABASE `jnotes_test_<guid>` and retries when CREATE contends. That needs CREATEDB; the official image's `POSTGRES_USER` is a superuser, so it is satisfied.
  - xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 (VSTest), so `--logger trx` works.
- **Postgres image:** `docker-compose.yml` and `docker-compose.debug.yml` both use `postgres:18-alpine`, and so will CI. The debug DB mounts tmpfs at `/var/lib/postgresql` (not `/data`, because of the postgres 18 PGDATA change) and runs with `fsync=off`. GitHub service containers can't override `command`, so CI keeps the tmpfs but drops the fsync flags.
- `Dockerfile`: two stages (`sdk:10.0` then `aspnet:10.0`). It restores the Web csproj and then runs `dotnet publish` **without** `--no-restore` (the documented gotcha). Final stage: `EXPOSE 8080`, `USER $APP_UID`, `VOLUME /data`, `ENTRYPOINT ["dotnet","JNotes.Web.dll"]`. It only copies `JNotes.slnx`, `Directory.Build.props`, `global.json`, csproj files and `src/`, so `.github/` never reaches the image. **No Dockerfile change is needed.**
- `.gitignore` already ignores `[Tt]est[Rr]esult*/`. `.dockerignore` needs no change.
- `.editorconfig` has no `end_of_line`, and there is no `.gitattributes`. That matters for `dotnet format --verify-no-changes` on Linux (see Risks).
- CLAUDE.md rules that apply:
  - The CI-style check is `dotnet format JNotes.slnx --verify-no-changes`.
  - "Docker build gotcha": verify the image contains `_framework/blazor.web.js`, not just that it builds.
  - **Never stop, remove, recreate or `down` the `jnotes-debug-*` containers.** Local tests use `jnotes-debug-db` on `localhost:5432`.
  - No Razor component tests.
- **Action major versions** (checked against GitHub release pages, Oct 2026):

  | Action | Pin | Latest release |
  |---|---|---|
  | `actions/checkout` | v7 | 7.0.1 |
  | `actions/setup-dotnet` | v6 | 6.0.0 (inputs `dotnet-version`, `cache`, `global-json-file`) |
  | `actions/cache` | v6 | 6.1.0 |
  | `actions/upload-artifact` | v7 | 7.0.2 |
  | `docker/setup-buildx-action` | v4 | 4.4.1 |
  | `docker/login-action` | v4 | 4.6.0 |
  | `docker/metadata-action` | v6 | 6.2.0 |
  | `docker/build-push-action` | v7 | 7.4.0 |

## Design decisions
1. **One reusable workflow called by both triggers.** `build-test.yml` (`on: workflow_call`) holds the only copy of the build and test steps. `ci.yml` (PRs) and `docker-publish.yml` (main, tags, manual) both call it. The publish job has `needs:` on it, so nothing is pushed unless tests pass.
2. **linux/amd64 only.** Building arm64 under QEMU emulates the whole `dotnet restore/publish` (5 to 15+ minutes extra, and .NET under QEMU is flaky). The right route to arm64 is a Dockerfile cross-compile (`FROM --platform=$BUILDPLATFORM` + `dotnet publish -a $TARGETARCH`); see Open questions. Out of scope here.
3. **Smoke-check the image before pushing.** The Dockerfile gotcha yields an image that builds fine but lacks `_framework/blazor.web.js`. The publish job builds once with `load: true`, checks the file exists, then runs the push build (all cache hits).
4. **Credentials:** variable `vars.DOCKERHUB_USERNAME` (not secret; making it a secret would mask "joeldroid" everywhere in logs) and secret `secrets.DOCKERHUB_TOKEN`. The image name `joeldroid/jnotes` is a workflow-level `env` constant.
5. **`dotnet format --verify-no-changes` is the last step of `build-test.yml`**, self-contained and deletable, so a format failure never hides test results. The executor must check it locally first (Phase 2).
6. **Security:** `permissions: contents: read` everywhere; `persist-credentials: false` on checkout; PRs use `pull_request` (never `pull_request_target`), so fork PRs get no secrets and need none; login and push only in the publish workflow, gated by `needs: build-test`, a ref check and `github.repository == 'joeldroid/JNotes'`; actions pinned to major tags.
7. **Concurrency:** PRs cancel superseded runs per PR. Publish runs one at a time per ref, never cancelled mid-push. The reusable workflow deliberately has **no** `concurrency` block (a called workflow sharing the caller's group would deadlock).

## Steps

### Phase 1: workflow files

1. **Create `.github/workflows/build-test.yml`** with exactly:

```yaml
# Reusable build + test job, called by ci.yml (pull requests) and docker-publish.yml (main/tags).
# The integration tests need a real PostgreSQL (see CLAUDE.md "Tests"): each test class creates and
# drops its own jnotes_test_<guid> database, so the user must have CREATEDB — the official image's
# POSTGRES_USER is a superuser. No concurrency block here on purpose: a called workflow sharing the
# caller's concurrency group would deadlock.
name: Build and test

on:
  workflow_call:

permissions:
  contents: read

env:
  DOTNET_NOLOGO: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true

jobs:
  build-test:
    name: Build and test
    runs-on: ubuntu-24.04
    timeout-minutes: 20

    services:
      postgres:
        # Same image as docker-compose.yml / docker-compose.debug.yml.
        image: postgres:18-alpine
        env:
          POSTGRES_DB: jnotes
          POSTGRES_USER: jnotes
          POSTGRES_PASSWORD: jnotes
        ports:
          - 5432:5432
        # tmpfs at /var/lib/postgresql (not .../data: postgres 18 keeps PGDATA in a versioned subfolder),
        # like jnotes-debug-db — throwaway data, faster CREATE/DROP DATABASE.
        options: >-
          --tmpfs /var/lib/postgresql
          --health-cmd "pg_isready -U jnotes -d jnotes"
          --health-interval 2s
          --health-timeout 5s
          --health-retries 15

    env:
      # Same as the tests' built-in default; explicit so CI doesn't silently depend on it.
      JNOTES_TEST_POSTGRES_CONNECTION_STRING: Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes

    steps:
      - name: Check out
        uses: actions/checkout@v7
        with:
          persist-credentials: false

      # global.json pins 10.0.100 with rollForward latestMinor, so the newest 10.0 SDK satisfies it.
      - name: Set up .NET
        uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x

      # No packages.lock.json in the repo, so setup-dotnet's own cache can't be used; key on the project files.
      - name: Cache NuGet packages
        uses: actions/cache@v6
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', 'Directory.Build.props', 'global.json') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      - name: Restore
        run: dotnet restore JNotes.slnx

      - name: Build
        run: dotnet build JNotes.slnx -c Release --no-restore

      - name: Test
        run: >-
          dotnet test JNotes.slnx -c Release --no-build
          --logger "trx;LogFileName=test-results.trx"
          --results-directory TestResults

      - name: Upload test results
        if: ${{ !cancelled() }}
        uses: actions/upload-artifact@v7
        with:
          name: test-results
          path: TestResults/
          if-no-files-found: ignore
          retention-days: 14

      # The CI-style format check from CLAUDE.md. Last, so a format problem never hides test results.
      # Safe to delete this step if it proves noisy.
      - name: Verify formatting
        run: dotnet format JNotes.slnx --verify-no-changes --no-restore
```

2. **Create `.github/workflows/ci.yml`:**

```yaml
# Pull requests into main: build and test only. Uses no secrets (fork PRs don't get any) and pushes nothing.
name: CI

on:
  pull_request:
    branches: [main]

permissions:
  contents: read

concurrency:
  group: ci-${{ github.event.pull_request.number }}
  cancel-in-progress: true

jobs:
  build-test:
    uses: ./.github/workflows/build-test.yml
```

3. **Create `.github/workflows/docker-publish.yml`:**

```yaml
# main / version tags: build and test, then build, smoke-check and push the image to Docker Hub.
# Needs repository variable DOCKERHUB_USERNAME and secret DOCKERHUB_TOKEN (a Docker Hub PAT, Read & Write).
name: Build, test and publish

on:
  push:
    branches: [main]
    tags: ['v*']
  workflow_dispatch:

permissions:
  contents: read

concurrency:
  group: publish-${{ github.ref }}
  cancel-in-progress: false

env:
  IMAGE_NAME: joeldroid/jnotes

jobs:
  build-test:
    uses: ./.github/workflows/build-test.yml

  docker:
    name: Build and push image
    needs: build-test
    # Only the real repo (forks have no Docker Hub credentials), and only main or v* tags
    # (a workflow_dispatch from another branch builds and tests but never pushes).
    if: >-
      github.repository == 'joeldroid/JNotes' &&
      (github.ref == 'refs/heads/main' || startsWith(github.ref, 'refs/tags/v'))
    runs-on: ubuntu-24.04
    timeout-minutes: 30

    steps:
      - name: Check out
        uses: actions/checkout@v7
        with:
          persist-credentials: false

      # latest = main; semver tags from v1.2.3 -> 1.2.3, 1.2, 1 (no bare major for 0.x); sha-<short> always.
      - name: Docker metadata
        id: meta
        uses: docker/metadata-action@v6
        with:
          images: ${{ env.IMAGE_NAME }}
          tags: |
            type=raw,value=latest,enable={{is_default_branch}}
            type=semver,pattern={{version}}
            type=semver,pattern={{major}}.{{minor}}
            type=semver,pattern={{major}},enable=${{ !startsWith(github.ref, 'refs/tags/v0.') }}
            type=sha

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v4

      # Build into the local daemon first so the image can be checked before anything is pushed.
      - name: Build image (local, for smoke test)
        uses: docker/build-push-action@v7
        with:
          context: .
          platforms: linux/amd64
          load: true
          push: false
          provenance: false
          tags: jnotes:smoke
          cache-from: type=gha,scope=jnotes
          cache-to: type=gha,scope=jnotes,mode=max

      # Guards the CLAUDE.md "Docker build gotcha": a stale static-web-assets manifest ships an image
      # without _framework/blazor.web.js that still builds and starts fine.
      - name: Smoke test image (Blazor script present)
        run: docker run --rm --entrypoint sh jnotes:smoke -c 'ls -l /app/wwwroot/_framework/blazor.web*.js'

      - name: Log in to Docker Hub
        uses: docker/login-action@v4
        with:
          username: ${{ vars.DOCKERHUB_USERNAME }}
          password: ${{ secrets.DOCKERHUB_TOKEN }}

      # Same inputs as above, so every layer is a cache hit; only the export/push is new work.
      - name: Build and push
        uses: docker/build-push-action@v7
        with:
          context: .
          platforms: linux/amd64
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
          annotations: ${{ steps.meta.outputs.annotations }}
          cache-from: type=gha,scope=jnotes
```

Note: the smoke-test path `/app/wwwroot/_framework/blazor.web*.js` is **UNVERIFIED**. .NET 10 may fingerprint the file name, hence the glob. Phase 3 checks it against a local image and adjusts the glob if needed.

### Phase 2: local verification of the workflows (the executor does this before finishing)

4. **Lint the YAML with actionlint** in a throwaway container (`--rm`; doesn't touch the debug containers). Run from the repo root:
   `docker run --rm -v "C:\Data\Code\JNotes:/repo" -w /repo rhysd/actionlint:latest -color`
   - Fix any reported error.
   - If Docker or the image isn't available, skip this and say so in the report.
5. **Build and test as CI does** (Windows host, against the standing `jnotes-debug-db`; the default connection string matches it). Check that it is running with `docker ps --filter name=jnotes-debug-db`. **Do not** start, recreate or `down` it. If it's not running, report that instead of starting it via compose.
   - `dotnet restore JNotes.slnx`
   - `dotnet build JNotes.slnx -c Release --no-restore`
   - `dotnet test JNotes.slnx -c Release --no-build --logger "trx;LogFileName=test-results.trx" --results-directory TestResults`
   - Expected: all tests pass and `TestResults\test-results.trx` exists. Delete `TestResults\` afterwards.
6. **Format check:** `dotnet format JNotes.slnx --verify-no-changes --no-restore`
   - If it reports changes, **do not reformat source files in this change**. Delete the "Verify formatting" step from `build-test.yml` and report it as a follow-up.
   - Also run `git ls-files --eol -- "*.cs" "*.razor"` and look for `i/crlf` entries. CRLF stored in the index would check out as CRLF on Linux and may fail `dotnet format` there even though it passes on Windows. If there are any, remove the format step and report it.
7. **Optional but recommended: run the tests on Linux** (they have only ever run on Windows). Throwaway SDK container sharing the debug DB's network namespace so `localhost:5432` works without changing that container; it copies the source and deletes the Windows `bin`/`obj` inside the container, so the host tree is untouched:
   `docker run --rm --network container:jnotes-debug-db -v "C:\Data\Code\JNotes:/repo:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c "cp -r /repo /work && cd /work && find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + && dotnet test JNotes.slnx -c Release"`
   - Expected: everything passes. Report any Linux-only failure (likely areas: file locking and paths; see Risks). Do not "fix" tests in this change.

### Phase 3: Docker image check (local, if Docker is available)

8. From the repo root:
   - `docker build -t jnotes:ci-local .`
   - `docker run --rm --entrypoint sh jnotes:ci-local -c 'ls -l /app/wwwroot/_framework/blazor.web*.js'`
   - Expected: at least one file is listed.
   - If it lists nothing, run `docker run --rm --entrypoint sh jnotes:ci-local -c 'find /app -name "blazor.web*"'` and change the glob in `docker-publish.yml` to match.
   - If the file genuinely isn't on disk in a known-good image, replace the check with a `find /app -name 'blazor.web*.js' | grep -q .` equivalent. If even that is impossible, remove the smoke step and explain why in the report.
   - Finally `docker image rm jnotes:ci-local`. Never `docker system prune` or anything else that could affect the debug containers or volumes.

### Phase 4: docs

9. **`README.md`:**
   - In the centered badge `<p>` (lines 11–16), add two badges as the first entries, in the existing `<img>` style but wrapped in links:
     - `<a href="https://github.com/joeldroid/JNotes/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/joeldroid/JNotes/actions/workflows/ci.yml/badge.svg"></a>`
     - `<a href="https://github.com/joeldroid/JNotes/actions/workflows/docker-publish.yml"><img alt="Publish" src="https://github.com/joeldroid/JNotes/actions/workflows/docker-publish.yml/badge.svg"></a>`
   - Add a short `### Continuous integration` subsection at the end of the "Running locally" section, right after "### Other commands" (before `## Running with Docker`), covering:
     - PRs to `main` run build, tests (against a `postgres:18-alpine` service) and the format check.
     - Pushes to `main` and `v*` tags additionally publish `joeldroid/jnotes` to Docker Hub with tags `latest` (main), `X.Y.Z`/`X.Y`/`X` (tags) and `sha-<short>`, `linux/amd64` only.
     - Publishing needs the repository variable `DOCKERHUB_USERNAME` and secret `DOCKERHUB_TOKEN`.
   - Optionally, in "## Running with Docker", add a sentence that a prebuilt image is available as `docker pull joeldroid/jnotes`. Do **not** change `docker-compose.yml` (it keeps `build: .`).
10. **`CLAUDE.md` — NOT TO BE EXECUTED.** The planner proposed appending a short "CI" paragraph to the "Docker build gotcha" section. `CLAUDE.md` is the user's guidance file and the user did not ask for it to be changed, so the executor must **leave it untouched**. The proposed text is kept here for the user to approve or discard:
    > **CI** (`.github/workflows/`): `ci.yml` (PRs) and `docker-publish.yml` (main, `v*` tags) both call the reusable `build-test.yml` (Linux, `postgres:18-alpine` service container, `dotnet format --verify-no-changes` last). The publish job builds `linux/amd64` only, and before pushing to `joeldroid/jnotes` it checks that `_framework/blazor.web*.js` exists inside the image, because of the gotcha above. Don't add a `concurrency` block to `build-test.yml`: one shared with a caller deadlocks.

## Secrets and variables the user must configure (not the executor)
1. **Docker Hub:** Account settings → Personal access tokens → Generate new token. Description "GitHub Actions JNotes"; access **Read & Write** (Delete not needed). Copy the token. The `joeldroid/jnotes` repository is created automatically on first push; pre-create it to choose public or private.
2. **GitHub:** `https://github.com/joeldroid/JNotes` → Settings → Secrets and variables → Actions.
   - **Secrets** tab → New repository secret: `DOCKERHUB_TOKEN` = the Docker Hub PAT.
   - **Variables** tab → New repository variable: `DOCKERHUB_USERNAME` = `joeldroid`.
   - To keep the username as a secret instead, add it as secret `DOCKERHUB_USERNAME` and change `vars.DOCKERHUB_USERNAME` to `secrets.DOCKERHUB_USERNAME` in `docker-publish.yml`.
3. Optional: branch protection on `main` requiring the check named **`build-test / Build and test`**.

## Tests to add or update
None. No product code changes; the existing xUnit suite is what the workflows run.

## Manual verification
- **Local** (executor, Phases 2–3): actionlint clean; `dotnet build`/`dotnet test -c Release` pass against `jnotes-debug-db`; `dotnet format --verify-no-changes` clean (or step removed and reported); optional Linux test run passes; local image contains `blazor.web*.js`.
- **On GitHub** (user, after committing):
  1. Open a PR to `main`. Expected: "CI" runs only `build-test / Build and test`, postgres service healthy, tests pass, `test-results` artifact attached, no Docker login/push steps.
  2. Push a second commit to the PR. Expected: the first run is cancelled.
  3. Merge to `main`. Expected: "Build, test and publish" runs `build-test` then `Build and push image`; smoke test lists the file; Docker Hub shows `latest` and `sha-<7 chars>`.
  4. `git tag v0.1.0 && git push origin v0.1.0`. Expected: tags `0.1.0` and `0.1`; no bare `0`; no `latest` from the tag build. A later `v1.0.0` also produces `1`.
  5. `docker pull joeldroid/jnotes:latest`, run against a throwaway DB and notes folder (never the user's `jnotes` database), open `http://localhost:8080` and confirm `/setup` or `/login` renders and is interactive after sign-in.
  6. A fork PR (if available) runs CI successfully without secrets.

## Risks and edge cases
- **The tests have never run on Linux.** The file-locking tests rely on `FileShare.None` refusing another handle: `tests/JNotes.Tests/Storage/FileSystemNoteFileStoreTests.cs:328` and `tests/JNotes.Tests/Integration/NoteSharingTests.cs:671`. On Unix, .NET emulates this with advisory `flock`, which should still refuse, but it is unverified (hence Phase 2 step 7). If either fails on Linux, report it; the fix is out of scope.
- **`dotnet format` on Linux vs Windows:** no `.gitattributes` and no `end_of_line`, so CRLF stored in git could fail on Linux only. Mitigated by the `git ls-files --eol` check; the step is easy to drop.
- **Postgres tuning:** CI can't pass `fsync=off` to a service container, so CREATE/DROP DATABASE is slower than on `jnotes-debug-db`. tmpfs mostly compensates. Raise the 20-minute timeout if real runs get close.
- **Parallel CREATE DATABASE contention** is handled by the existing 3-attempt retry; slower CI disks could hit it more. Watch for flaky `PostgresException` failures.
- **Floating SDK:** `10.0.x` follows new SDK feature bands; a new analyzer could fail the build or format check with no code change. Alternative: pin `dotnet-version` to a specific 10.0.1xx patch.
- **Tag triggers:** non-semver tags matching `v*` (e.g. `v1`) only get `sha-…`. A tag and a main push of the same commit both publish (harmless). Every push to `main`, docs-only included, republishes `latest` (cached builds take ~1 min). No path filters, since they also block required checks.
- **GHA cache:** 10 GB per repo with eviction. `mode=max` caches all stages; switch to `mode=min` if eviction thrashes.
- **Smoke-test path** is UNVERIFIED until Phase 3.
- **Major-tag pins** are mutable. SHA pinning with Dependabot is the stricter option (Open question 3).

## Open questions
1. **arm64 image?** Default: no (amd64 only). If wanted, the recommended route is a Dockerfile cross-compile (`--platform=$BUILDPLATFORM`, `dotnet publish -a $TARGETARCH`) plus `platforms: linux/amd64,linux/arm64`, not QEMU.
2. **Docker build in PR CI?** Building the image (no push) in `ci.yml` would catch Dockerfile breakage before merge, at ~2–3 extra minutes. Default: not included.
3. **SHA-pinned actions plus Dependabot** (`.github/dependabot.yml` for `github-actions` and `nuget`)? Default: major-tag pins, no Dependabot.
4. **Should `latest` follow `main` or the newest release tag?** Default: `main`. For the newest release, set `flavor: latest=auto` and drop the `type=raw` line.

## Out of scope
- Any change to `Dockerfile`, `.dockerignore`, `docker-compose.yml`, `docker-compose.debug.yml`, `global.json`, csproj files, `CLAUDE.md`, or any source or test code. That includes reformatting code to satisfy `dotnet format`.
- Stopping, removing, recreating or running `docker compose up/down` for the `jnotes-debug-*` containers or their volumes.
- Creating or committing any secret, token or `.env`. The Docker Hub and GitHub settings above are the user's job.
- GHCR publishing, release notes or GitHub Releases, Docker Hub description sync, SBOM or signing, code coverage, Dependabot config, path filters.
