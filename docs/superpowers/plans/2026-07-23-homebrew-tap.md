# RepoGlean Homebrew Tap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish RepoGlean through `aczarkowski/homebrew-tap`, verify a real Homebrew installation, and automatically open reviewed formula-update pull requests for new stable RepoGlean releases.

**Architecture:** The tap installs the existing immutable Native AOT archives through one OS/CPU-selecting formula. A small dependency-free Ruby library validates GitHub release metadata and deterministically renders that formula; a scheduled workflow runs the same tested updater and opens a pull request when rendering changes.

**Tech Stack:** Homebrew formula DSL, Ruby standard library, Minitest, GitHub Actions, GitHub Releases, Git, .NET 10.

## Global Constraints

- Create the public repository `github.com/aczarkowski/homebrew-tap` with `master` as its default branch.
- Publish the formula as `Formula/repoglean.rb` and install it with `brew install aczarkowski/tap/repoglean`.
- Support exactly macOS ARM64/x64 and Linux ARM64/x64 using immutable tagged release archives.
- RepoGlean version `2.0.0` and the four SHA-256 values must match the approved design specification exactly.
- Declare Homebrew `git` as a runtime dependency.
- `brew livecheck aczarkowski/tap/repoglean` must use GitHub's latest stable release.
- Update checks run daily at `06:17 UTC` and through `workflow_dispatch`.
- Automation validates a stable semantic-version tag, all eight required assets, and 64-character lowercase hexadecimal checksums.
- Automation opens or updates a pull request; it never writes a new formula version directly to `master`.
- Use only the tap repository's scoped `GITHUB_TOKEN`; add no repository secret or cross-repository credential.
- Preserve the existing RepoGlean `v2.0.0` tag and release unchanged.
- Updater code must run on the current local Ruby `2.6.10` without third-party gems.

---

### Task 1: Bootstrap the standard tap repository

**Files:**
- Create repository: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap`
- Generated: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/.github/dependabot.yml`
- Generated: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/.github/workflows/tests.yml`
- Generated: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/.github/workflows/publish.yml`
- Modify: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/README.md`

**Interfaces:**
- Consumes: Homebrew 6's `tap-new` templates and the approved `master` default branch.
- Produces: a local `aczarkowski/tap` Git repository ready for formula development.

- [ ] **Step 1: Verify the tap does not already exist locally or remotely**

Run:

```bash
brew tap | grep -Fx 'aczarkowski/tap' && exit 1 || true
test ! -e /opt/homebrew/Library/Taps/aczarkowski/homebrew-tap
curl -fsS https://api.github.com/repos/aczarkowski/homebrew-tap && exit 1 || true
```

Expected: the local path is absent and the GitHub API returns `404`.

- [ ] **Step 2: Generate Homebrew's current tap structure**

Run:

```bash
brew tap-new --branch=master aczarkowski/tap
```

Expected: Homebrew creates and commits the tap at
`/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap` with `master` checked out.

- [ ] **Step 3: Replace the generated README with the RepoGlean tap contract**

Write:

````markdown
# RepoGlean Homebrew Tap

Homebrew distribution for [RepoGlean](https://github.com/aczarkowski/RepoGlean),
a CLI that safely reclaims space from regenerable Git artifacts.

## Install

```bash
brew install aczarkowski/tap/repoglean
```

Or tap first:

```bash
brew tap aczarkowski/tap
brew install repoglean
```

## Updates

```bash
brew update
brew outdated repoglean
brew upgrade repoglean
```

Maintainers can compare the formula with the latest stable GitHub release:

```bash
brew livecheck aczarkowski/tap/repoglean
```
````

- [ ] **Step 4: Verify and commit the bootstrap**

Run:

```bash
git diff --check
git add README.md
git commit -m "docs: introduce RepoGlean tap"
```

Expected: a second local tap commit records only the RepoGlean README.

---

### Task 2: Implement the release validator and deterministic formula renderer

**Files:**
- Create: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/test/repoglean_formula_test.rb`
- Create: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/lib/repoglean_formula.rb`

**Interfaces:**
- Consumes: parsed GitHub `releases/latest` hashes and a callable checksum loader.
- Produces: `RepoGleanFormula.parse_release(payload, checksum_loader:)`,
  `RepoGleanFormula.render(release)`, and `RepoGleanFormula.current_version(path)`.

- [ ] **Step 1: Write failing rendering and no-op tests**

Create `test/repoglean_formula_test.rb`:

```ruby
# frozen_string_literal: true

require "json"
require "minitest/autorun"
require "tmpdir"
require "repoglean_formula"

class RepoGleanFormulaTest < Minitest::Test
  SHA256 = {
    "osx-arm64" => "2" * 64,
    "osx-x64" => "3" * 64,
    "linux-arm64" => "4" * 64,
    "linux-x64" => "5" * 64,
  }.freeze

  def release_payload(tag: "v2.0.0", draft: false, prerelease: false)
    assets = RepoGleanFormula::RIDS.flat_map do |rid|
      archive = "repoglean-#{rid}.tar.gz"
      [
        { "name" => archive, "browser_download_url" => "https://example.test/#{archive}" },
        { "name" => "#{archive}.sha256", "browser_download_url" => "https://example.test/#{archive}.sha256" },
      ]
    end
    { "tag_name" => tag, "draft" => draft, "prerelease" => prerelease, "assets" => assets }
  end

  def checksum_loader(overrides = {})
    lambda do |url|
      name = File.basename(URI(url).path)
      overrides.fetch(name) do
        rid = name.delete_prefix("repoglean-").delete_suffix(".tar.gz.sha256")
        "#{SHA256.fetch(rid)}  #{name.delete_suffix(".sha256")}\n"
      end
    end
  end

  def test_renders_all_platforms_and_formula_contract
    release = RepoGleanFormula.parse_release(release_payload, checksum_loader: checksum_loader)
    rendered = RepoGleanFormula.render(release)

    assert_equal "2.0.0", release.version
    RepoGleanFormula::RIDS.each do |rid|
      assert_includes rendered, "https://example.test/repoglean-#{rid}.tar.gz"
      assert_includes rendered, SHA256.fetch(rid)
    end
    assert_includes rendered, 'depends_on "git"'
    assert_includes rendered, "strategy :github_latest"
    assert_includes rendered, 'assert_equal "repoglean #{version}\\n"'

    Dir.mktmpdir do |directory|
      path = File.join(directory, "repoglean.rb")
      File.write(path, rendered)
      assert_equal "2.0.0", RepoGleanFormula.current_version(path)
    end
  end
end
```

- [ ] **Step 2: Run the tests and verify RED**

Run:

```bash
ruby -Ilib test/repoglean_formula_test.rb
```

Expected: FAIL with `cannot load such file -- repoglean_formula`.

- [ ] **Step 3: Implement the minimal public types and renderer**

Create `lib/repoglean_formula.rb` with:

```ruby
# frozen_string_literal: true

require "uri"

module RepoGleanFormula
  RIDS = %w[osx-arm64 osx-x64 linux-arm64 linux-x64].freeze
  Release = Struct.new(:version, :archives, :checksums, keyword_init: true)

  module_function

  def parse_release(payload, checksum_loader:)
    version = payload.fetch("tag_name").delete_prefix("v")
    assets = payload.fetch("assets").to_h { |asset| [asset.fetch("name"), asset.fetch("browser_download_url")] }
    archives = {}
    checksums = {}

    RIDS.each do |rid|
      archive_name = "repoglean-#{rid}.tar.gz"
      checksum_name = "#{archive_name}.sha256"
      archives[rid] = assets.fetch(archive_name)
      checksums[rid] = checksum_loader.call(assets.fetch(checksum_name)).split.first
    end

    Release.new(version: version, archives: archives, checksums: checksums)
  end

  def current_version(path)
    match = File.read(path).match(/^  version "([^"]+)"$/)
    raise ArgumentError, "formula version is missing" unless match

    match[1]
  end

  def render(release)
    <<~RUBY
      class RepoGlean < Formula
        desc "Safely reclaim space from regenerable Git artifacts"
        homepage "https://github.com/aczarkowski/RepoGlean"
        version "#{release.version}"
        license "MIT"

        on_macos do
          if Hardware::CPU.arm?
            url "#{release.archives.fetch("osx-arm64")}"
            sha256 "#{release.checksums.fetch("osx-arm64")}"
          else
            url "#{release.archives.fetch("osx-x64")}"
            sha256 "#{release.checksums.fetch("osx-x64")}"
          end
        end

        on_linux do
          if Hardware::CPU.arm?
            url "#{release.archives.fetch("linux-arm64")}"
            sha256 "#{release.checksums.fetch("linux-arm64")}"
          else
            url "#{release.archives.fetch("linux-x64")}"
            sha256 "#{release.checksums.fetch("linux-x64")}"
          end
        end

        livecheck do
          url :stable
          strategy :github_latest
        end

        depends_on "git"

        def install
          rid = if OS.mac?
            Hardware::CPU.arm? ? "osx-arm64" : "osx-x64"
          else
            Hardware::CPU.arm? ? "linux-arm64" : "linux-x64"
          end
          bin.install "repoglean-\#{rid}/repoglean"
        end

        test do
          assert_equal "repoglean \#{version}\\n", shell_output("\#{bin}/repoglean --version")
        end
      end
    RUBY
  end
end
```

- [ ] **Step 4: Run the tests and verify GREEN**

Run:

```bash
ruby -Ilib test/repoglean_formula_test.rb
```

Expected: all rendering and current-version assertions pass.

- [ ] **Step 5: Add failing validation tests**

Add:

```ruby
def assert_release_error(message, payload: release_payload, loader: checksum_loader)
  error = assert_raises(ArgumentError) do
    RepoGleanFormula.parse_release(payload, checksum_loader: loader)
  end
  assert_equal message, error.message
end

def test_rejects_malformed_tag
  assert_release_error(
    "release tag must be v<major>.<minor>.<patch>",
    payload: release_payload(tag: "release-2.0.0"),
  )
end

def test_rejects_draft_and_prerelease
  assert_release_error("release is not stable", payload: release_payload(draft: true))
  assert_release_error("release is not stable", payload: release_payload(prerelease: true))
end

def test_rejects_each_missing_asset
  release_payload.fetch("assets").each do |asset|
    payload = release_payload
    payload["assets"].reject! { |candidate| candidate.fetch("name") == asset.fetch("name") }
    assert_release_error("missing asset: #{asset.fetch("name")}", payload: payload)
  end
end

def test_rejects_malformed_checksum
  name = "repoglean-osx-arm64.tar.gz.sha256"
  assert_release_error(
    "invalid checksum: #{name}",
    loader: checksum_loader(name => "not-a-checksum\n"),
  )
end
```

- [ ] **Step 6: Run validation tests and verify RED, then GREEN**

Temporarily run before completing any missing validation:

```bash
ruby -Ilib test/repoglean_formula_test.rb
```

Expected RED: malformed tag, draft/prerelease, missing-asset, and checksum
assertions fail for those intended reasons. Replace `parse_release` with:

```ruby
def parse_release(payload, checksum_loader:)
  raise ArgumentError, "release is not stable" if payload.fetch("draft") || payload.fetch("prerelease")

  match = /\Av(\d+\.\d+\.\d+)\z/.match(payload.fetch("tag_name"))
  raise ArgumentError, "release tag must be v<major>.<minor>.<patch>" unless match

  assets = payload.fetch("assets").to_h { |asset| [asset.fetch("name"), asset.fetch("browser_download_url")] }
  archives = {}
  checksums = {}

  RIDS.each do |rid|
    archive_name = "repoglean-#{rid}.tar.gz"
    checksum_name = "#{archive_name}.sha256"
    archives[rid] = assets.fetch(archive_name) { raise ArgumentError, "missing asset: #{archive_name}" }
    checksum_url = assets.fetch(checksum_name) { raise ArgumentError, "missing asset: #{checksum_name}" }
    checksum = checksum_loader.call(checksum_url).split.first
    unless /\A[0-9a-f]{64}\z/.match?(checksum)
      raise ArgumentError, "invalid checksum: #{checksum_name}"
    end
    checksums[rid] = checksum
  end

  Release.new(version: match[1], archives: archives, checksums: checksums)
end
```

Rerun and require all tests to pass.

- [ ] **Step 7: Commit the tested renderer**

Run:

```bash
git add lib/repoglean_formula.rb test/repoglean_formula_test.rb
git commit -m "feat: render RepoGlean formula from releases"
```

---

### Task 3: Add the updater CLI and generate the v2.0.0 formula

**Files:**
- Create: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/script/update-repoglean`
- Create: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/Formula/repoglean.rb`
- Modify: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/test/repoglean_formula_test.rb`

**Interfaces:**
- Consumes: `RepoGleanFormula.parse_release` and `.render`, GitHub's public
  `releases/latest` JSON, and checksum asset bodies.
- Produces: an executable updater that writes `Formula/repoglean.rb` atomically
  and prints either `updated repoglean to <version>` or
  `repoglean <version> is current`.

- [ ] **Step 1: Write a failing CLI contract test**

Add `require "open3"` and:

```ruby
def test_cli_updates_then_reports_current
  Dir.mktmpdir do |directory|
    release_path = File.join(directory, "release.json")
    formula_path = File.join(directory, "repoglean.rb")
    File.write(release_path, JSON.generate(release_payload))
    RepoGleanFormula::RIDS.each do |rid|
      name = "repoglean-#{rid}.tar.gz.sha256"
      File.write(File.join(directory, name), "#{SHA256.fetch(rid)}  #{name.delete_suffix(".sha256")}\n")
    end

    environment = {
      "REPOGLEAN_RELEASE_JSON" => release_path,
      "REPOGLEAN_CHECKSUM_DIRECTORY" => directory,
      "REPOGLEAN_FORMULA_PATH" => formula_path,
    }
    command = [File.expand_path("../script/update-repoglean", __dir__)]

    stdout, stderr, status = Open3.capture3(environment, *command)
    assert status.success?, stderr
    assert_equal "updated repoglean to 2.0.0\n", stdout

    first_bytes = File.binread(formula_path)
    stdout, stderr, status = Open3.capture3(environment, *command)
    assert status.success?, stderr
    assert_equal "repoglean 2.0.0 is current\n", stdout
    assert_equal first_bytes, File.binread(formula_path)
  end
end
```

- [ ] **Step 2: Run the CLI test and verify RED**

Run:

```bash
ruby -Ilib test/repoglean_formula_test.rb
```

Expected: FAIL because `script/update-repoglean` does not exist.

- [ ] **Step 3: Implement the updater CLI**

Create the executable:

```ruby
#!/usr/bin/env ruby
# frozen_string_literal: true

require "json"
require "net/http"
require "pathname"
require "tempfile"
require "uri"
require_relative "../lib/repoglean_formula"

RELEASE_URL = "https://api.github.com/repos/aczarkowski/RepoGlean/releases/latest"

def get(url)
  uri = URI(url)
  request = Net::HTTP::Get.new(uri)
  request["Accept"] = "application/vnd.github+json"
  request["X-GitHub-Api-Version"] = "2022-11-28"
  request["User-Agent"] = "aczarkowski-homebrew-tap"
  response = Net::HTTP.start(uri.hostname, uri.port, use_ssl: uri.scheme == "https") do |http|
    http.request(request)
  end
  raise "GET #{url} returned #{response.code}" unless response.is_a?(Net::HTTPSuccess)

  response.body
end

release_json = if ENV["REPOGLEAN_RELEASE_JSON"]
  File.read(ENV.fetch("REPOGLEAN_RELEASE_JSON"))
else
  get(RELEASE_URL)
end

checksum_loader = lambda do |url|
  if ENV["REPOGLEAN_CHECKSUM_DIRECTORY"]
    filename = File.basename(URI(url).path)
    File.read(File.join(ENV.fetch("REPOGLEAN_CHECKSUM_DIRECTORY"), filename))
  else
    get(url)
  end
end

release = RepoGleanFormula.parse_release(JSON.parse(release_json), checksum_loader: checksum_loader)
rendered = RepoGleanFormula.render(release)
root = Pathname(__dir__).parent
target = Pathname(ENV.fetch("REPOGLEAN_FORMULA_PATH", root/"Formula/repoglean.rb"))

if target.exist? && target.binread == rendered
  puts "repoglean #{release.version} is current"
  exit 0
end

target.dirname.mkpath
Tempfile.create([target.basename.to_s, ".tmp"], target.dirname) do |temporary|
  temporary.binmode
  temporary.write(rendered)
  temporary.flush
  File.rename(temporary.path, target)
end
puts "updated repoglean to #{release.version}"
```

Run `chmod +x script/update-repoglean`.

- [ ] **Step 4: Run the CLI test and verify GREEN**

Run:

```bash
ruby -Ilib test/repoglean_formula_test.rb
```

Expected: all library and CLI tests pass.

- [ ] **Step 5: Generate the formula from the live v2.0.0 release**

Run:

```bash
script/update-repoglean
git diff -- Formula/repoglean.rb
```

Expected: the updater prints `updated repoglean to 2.0.0`; the formula contains
the approved four URLs and SHA-256 values.

- [ ] **Step 6: Verify formula syntax, style, audit, and livecheck**

Run:

```bash
ruby -c Formula/repoglean.rb
brew style aczarkowski/tap/repoglean
brew audit --strict --online aczarkowski/tap/repoglean
brew livecheck aczarkowski/tap/repoglean
```

Expected: Ruby syntax, style, and audit succeed; livecheck reports `2.0.0` as
current/latest.

- [ ] **Step 7: Commit the updater and formula**

Run:

```bash
git add Formula/repoglean.rb script/update-repoglean test/repoglean_formula_test.rb
git commit -m "feat: package RepoGlean for Homebrew"
```

---

### Task 4: Add tested update-PR automation

**Files:**
- Create: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/.github/workflows/update-repoglean.yml`
- Modify: `/opt/homebrew/Library/Taps/aczarkowski/homebrew-tap/test/repoglean_formula_test.rb`

**Interfaces:**
- Consumes: `script/update-repoglean`, Homebrew validation commands, the
  repository-scoped `GITHUB_TOKEN`, and GitHub CLI.
- Produces: daily/manual no-op checks and reviewed update PRs on
  `automation/repoglean-<version>`.

- [ ] **Step 1: Add a failing workflow contract test**

Add a test that parses `.github/workflows/update-repoglean.yml` with
`YAML.safe_load(File.read(workflow_path))`. YAML 1.1 may parse the unquoted `on`
key as boolean `true`, so normalize with
`triggers = workflow["on"] || workflow.fetch(true)`.
Assert:

```ruby
assert_equal "17 6 * * *", triggers.fetch("schedule").fetch(0).fetch("cron")
assert triggers.key?("workflow_dispatch")
assert_equal "write", workflow.fetch("permissions").fetch("contents")
assert_equal "write", workflow.fetch("permissions").fetch("pull-requests")
source = File.read(workflow_path)
refute_includes source, "secrets."
refute_includes source, "push origin master"
%w[
  test/repoglean_formula_test.rb
  script/update-repoglean
  brew\ style
  brew\ audit\ --strict\ --online
  brew\ livecheck
  gh\ pr\ create
].each { |command| assert_includes source, command }
```

Add `require "yaml"` at the top of the test file and define
`workflow_path = File.expand_path("../.github/workflows/update-repoglean.yml", __dir__)`.

- [ ] **Step 2: Run the test and verify RED**

Run:

```bash
ruby -Ilib test/repoglean_formula_test.rb
```

Expected: FAIL because `.github/workflows/update-repoglean.yml` is missing.

- [ ] **Step 3: Implement the update workflow**

Create:

```yaml
name: Update RepoGlean

on:
  schedule:
    - cron: "17 6 * * *"
  workflow_dispatch:

permissions:
  contents: write
  pull-requests: write

concurrency:
  group: update-repoglean
  cancel-in-progress: false

jobs:
  update:
    runs-on: macos-15
    steps:
      - uses: actions/checkout@v6

      - name: Test updater
        run: ruby -Ilib test/repoglean_formula_test.rb

      - name: Update formula
        id: update
        run: |
          script/update-repoglean
          if git diff --quiet -- Formula/repoglean.rb; then
            echo "changed=false" >> "$GITHUB_OUTPUT"
          else
            echo "changed=true" >> "$GITHUB_OUTPUT"
          fi

      - name: Validate formula
        if: steps.update.outputs.changed == 'true'
        run: |
          brew style Formula/repoglean.rb
          brew audit --strict --online Formula/repoglean.rb
          brew livecheck Formula/repoglean.rb

      - name: Open update pull request
        if: steps.update.outputs.changed == 'true'
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          version=$(ruby -Ilib -rrepoglean_formula \
            -e 'puts RepoGleanFormula.current_version("Formula/repoglean.rb")')
          branch="automation/repoglean-${version}"
          git config user.name "github-actions[bot]"
          git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
          git switch -c "$branch"
          git add Formula/repoglean.rb
          git commit -m "repoglean ${version}"

          remote_sha=$(git ls-remote --heads origin "refs/heads/${branch}" | awk '{print $1}')
          if [ -n "$remote_sha" ]; then
            git push \
              --force-with-lease="refs/heads/${branch}:${remote_sha}" \
              origin "HEAD:refs/heads/${branch}"
          else
            git push origin "HEAD:refs/heads/${branch}"
          fi

          pr_number=$(gh pr list --head "$branch" --state all \
            --json number --jq '.[0].number // empty')
          if [ -z "$pr_number" ]; then
            gh pr create \
              --base master \
              --head "$branch" \
              --title "repoglean ${version}" \
              --body "Automated update to RepoGlean ${version}."
          else
            gh pr reopen "$pr_number" 2>/dev/null || true
          fi
```

- [ ] **Step 4: Run tests and workflow lint**

Run:

```bash
command -v actionlint >/dev/null || brew install actionlint
ruby -Ilib test/repoglean_formula_test.rb
actionlint .github/workflows/*.yml
```

Expected: all tests pass and actionlint reports no findings.

- [ ] **Step 5: Commit the workflow**

Run:

```bash
git add .github/workflows/update-repoglean.yml test/repoglean_formula_test.rb
git commit -m "ci: propose RepoGlean formula updates"
```

---

### Task 5: Create and publish `aczarkowski/homebrew-tap`

**Files:**
- External repository: `https://github.com/aczarkowski/homebrew-tap`
- Local published checkout: `/Users/andrzej/GitHub/homebrew-tap`

**Interfaces:**
- Consumes: the verified local tap commit history.
- Produces: a public GitHub repository with `master`, enabled Actions PR
  creation, and the complete tap source.

- [ ] **Step 1: Run the complete pre-publish tap verification**

Run:

```bash
ruby -Ilib test/repoglean_formula_test.rb
ruby -c Formula/repoglean.rb
actionlint .github/workflows/*.yml
brew style aczarkowski/tap/repoglean
brew audit --strict --online aczarkowski/tap/repoglean
brew livecheck aczarkowski/tap/repoglean
git diff --check
git status --short
```

Expected: every command succeeds and the worktree is clean.

- [ ] **Step 2: Create the public GitHub repository**

Using the signed-in GitHub session, create:

```text
owner: aczarkowski
name: homebrew-tap
visibility: public
initialize: false
default branch: master
```

Do not add a generated README, license, or `.gitignore`.

- [ ] **Step 3: Enable workflow pull-request creation**

In `homebrew-tap` repository Actions settings, select read/write workflow
permissions and enable GitHub Actions to create pull requests. Make no broader
account-wide permission change.

- [ ] **Step 4: Push the verified local repository**

Run:

```bash
git remote add origin git@github.com:aczarkowski/homebrew-tap.git
git push -u origin master
git ls-remote origin refs/heads/master
```

Expected: remote `master` resolves to the local verified HEAD.

- [ ] **Step 5: Establish the normal local checkout**

After the push is confirmed, untap the development checkout and clone the
published repository:

```bash
brew untap aczarkowski/tap
git clone git@github.com:aczarkowski/homebrew-tap.git /Users/andrzej/GitHub/homebrew-tap
```

Expected: `/Users/andrzej/GitHub/homebrew-tap` is clean on `master`; the
Homebrew tap path is absent before consumer installation.

---

### Task 6: Document Homebrew in RepoGlean

**Files:**
- Modify: `/Users/andrzej/GitHub/DevCleaner/README.md`

**Interfaces:**
- Consumes: the published `aczarkowski/tap/repoglean` formula.
- Produces: installation, update discovery, and upgrade instructions in the
  main product README.

- [ ] **Step 1: Create an isolated RepoGlean worktree**

Use `superpowers:using-git-worktrees` to create:

```text
branch: codex/homebrew-tap
path: /Users/andrzej/GitHub/DevCleaner/.worktrees/homebrew-tap
```

Run the full RepoGlean baseline before editing.

- [ ] **Step 2: Add the Homebrew installation section**

At the start of `## Install`, add:

````markdown
### Homebrew

On macOS or Linux:

```bash
brew install aczarkowski/tap/repoglean
```

Check for a newer stable release and upgrade with:

```bash
brew livecheck aczarkowski/tap/repoglean
brew update
brew upgrade repoglean
```

### Release archives
````

Keep the existing platform archive table under `### Release archives`.

- [ ] **Step 3: Verify documentation and the full solution**

Run:

```bash
rg -n 'brew install aczarkowski/tap/repoglean|brew livecheck aczarkowski/tap/repoglean|brew upgrade repoglean' README.md
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx -c Release --no-restore -warnaserror
dotnet test RepoGlean.slnx -c Release --no-build
git diff --check
```

Expected: all three commands occur in the README, build succeeds without
warnings, and all 261 tests pass.

- [ ] **Step 4: Commit and integrate locally**

Run:

```bash
git add README.md
git commit -m "docs: add Homebrew installation"
```

Fast-forward the verified worktree into root `master`, remove the worktree and
feature branch, then rerun `git status --short --branch`.

---

### Task 7: Consumer installation and automation acceptance

**Files:**
- Installed binary: `$(brew --prefix)/bin/repoglean`
- External workflows:
  `https://github.com/aczarkowski/homebrew-tap/actions`

**Interfaces:**
- Consumes: both published repositories and Homebrew's normal HTTPS tap flow.
- Produces: fresh evidence that users can install, test, detect updates, and run
  RepoGlean, plus evidence that the scheduled updater no-ops safely at v2.0.0.

- [ ] **Step 1: Push the RepoGlean documentation commits**

Run:

```bash
git push origin master
git ls-remote origin refs/heads/master
```

Expected: remote `master` matches local `HEAD`.

- [ ] **Step 2: Perform a fresh consumer installation from GitHub**

Ensure no development tap remains, then run:

```bash
brew uninstall repoglean 2>/dev/null || true
brew untap aczarkowski/tap 2>/dev/null || true
brew install aczarkowski/tap/repoglean
```

Expected: Homebrew clones `aczarkowski/homebrew-tap`, verifies the ARM64 macOS
archive checksum, installs the declared Git dependency if needed, and links
`repoglean`.

- [ ] **Step 3: Verify the installed formula**

Run:

```bash
brew test aczarkowski/tap/repoglean
brew livecheck aczarkowski/tap/repoglean
test "$(repoglean --version)" = "repoglean 2.0.0"
brew info aczarkowski/tap/repoglean
```

Expected: formula test passes, livecheck reports `2.0.0` as current, version
comparison succeeds, and `brew info` identifies the tap formula.

- [ ] **Step 4: Verify GitHub CI**

Wait for the tap's pushed `brew test-bot` run and require every job to complete
successfully. If Homebrew's generated bottle workflow is not relevant to the
precompiled formula, require it to be skipped rather than failed.

- [ ] **Step 5: Manually dispatch the update workflow**

Using the signed-in GitHub session, dispatch `Update RepoGlean` on `master`.
Require the run to pass and its updater step to report:

```text
repoglean 2.0.0 is current
```

Verify that no update pull request or automation branch was created for this
no-op run.

- [ ] **Step 6: Run final repository checks**

RepoGlean:

```bash
git status --short --branch
dotnet build RepoGlean.slnx -c Release --no-restore -warnaserror
dotnet test RepoGlean.slnx -c Release --no-build
```

Tap:

```bash
git -C /Users/andrzej/GitHub/homebrew-tap status --short --branch
ruby -I/Users/andrzej/GitHub/homebrew-tap/lib \
  /Users/andrzej/GitHub/homebrew-tap/test/repoglean_formula_test.rb
brew style aczarkowski/tap/repoglean
brew audit --strict --online aczarkowski/tap/repoglean
```

Expected: both repositories are clean and synchronized, 261 RepoGlean tests
pass, updater tests pass, and Homebrew style/audit succeed.
