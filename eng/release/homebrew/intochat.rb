cask "intochat" do
  arch arm: "arm64", intel: "x64"

  version "0.17.0"
  sha256 arm:   "0000000000000000000000000000000000000000000000000000000000000000",
         intel: "0000000000000000000000000000000000000000000000000000000000000000"

  url "https://releases.intochat.io/stable/#{version}/intochat-#{version}-osx-#{arch}.tar.gz"
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
