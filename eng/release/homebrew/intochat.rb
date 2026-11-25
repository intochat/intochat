# Template cask: version, channel, and SHA-256 digests are populated during release packaging (eng/package.cs).
cask "intochat" do
  arch arm: "arm64", intel: "x64"

  version "PENDING_RELEASE_VERSION"
  sha256 arm:   "PENDING_RELEASE_SHA256_OSX_ARM64",
         intel: "PENDING_RELEASE_SHA256_OSX_X64"

  url "https://releases.intochat.io/PENDING_RELEASE_CHANNEL/#{version}/intochat-#{version}-osx-#{arch}.tar.gz"
  name "IntoChat OS"
  desc "Local-first AI operating system with Ino, live C# micro-apps, and ArtCraft studios"
  homepage "https://intochat.io"

  depends_on macos: ">= :sonoma"

  binary "#{staged_path}/intochat"

  postflight do
    system_command "/usr/bin/xattr",
                   args: ["-dr", "com.apple.quarantine", staged_path.to_s],
                   must_succeed: false
  end

  uninstall signal: ["TERM", "IntoChat.OS.Desktop"]

  zap trash: [
    "~/.intochat/logs",
    "~/.intochat/run",
    "~/.intochat/state",
  ]
end
