# Z+ — Linux build
#
# Builds the three Linux binaries (net10.0, self-contained single files):
#   publish/linux/server/zplus-server   — the full Z+ server
#   publish/linux/client/zplus-client   — desktop meeting client (Avalonia GUI)
#   publish/linux/admin/zplus-admin     — desktop admin console (Avalonia GUI)
#
# Requirements: GNU make + .NET 10 SDK on the build machine (Linux or Windows).
# Run from the repository root:
#   make            # build all three
#   make server     # just the server
#   make clean      # remove publish/linux and intermediate outputs
#
# The Windows GUI apps are not built here — publish those on Windows
# (see README, "Building single-file executables").

CONFIG ?= Release
TFM    ?= net10.0
OUT    ?= publish/linux
DOTNET ?= dotnet

# ---- RuntimeIdentifier auto-detection ---------------------------------------
# The RID is derived from the build machine: x86_64 -> linux-x64,
# aarch64 -> linux-arm64, armv7l/armv6l (armhf) -> linux-arm, with a
# linux-musl-* variant on musl-based systems (e.g. Alpine).
# Cross-build by passing it explicitly:  make RID=linux-arm64
ifeq ($(origin RID), undefined)
  UNAME_M := $(shell uname -m)
  ifeq ($(UNAME_M),x86_64)
    RID_ARCH := x64
  else ifeq ($(UNAME_M),amd64)
    RID_ARCH := x64
  else ifeq ($(UNAME_M),aarch64)
    RID_ARCH := arm64
  else ifeq ($(UNAME_M),arm64)
    RID_ARCH := arm64
  else ifeq ($(UNAME_M),armv7l)
    RID_ARCH := arm
  else ifeq ($(UNAME_M),armv6l)
    RID_ARCH := arm
  else
    $(error Cannot map architecture '$(UNAME_M)' to a .NET RID - pass RID=<linux-rid> explicitly)
  endif
  RID_LIBC := $(shell ldd --version 2>&1 | grep -qi musl && echo musl-)
  RID := linux-$(RID_LIBC)$(RID_ARCH)
endif

SINGLE_FILE = --self-contained \
              -p:PublishSingleFile=true \
              -p:IncludeNativeLibrariesForSelfExtract=true \
              -p:EnableCompressionInSingleFile=true

.PHONY: all server client admin clean

all: server client admin
	@echo ""
	@echo "Done. Linux binaries ($(TFM), $(RID)):"
	@ls -l "$(OUT)/server/zplus-server" "$(OUT)/client/zplus-client" "$(OUT)/admin/zplus-admin"

# The server project already embeds the single-file/self-contained settings;
# its assembly is named "Z+ Server", so rename the binary for shell friendliness.
server:
	$(DOTNET) publish src/ZPlus.Server -c $(CONFIG) -f $(TFM) -r $(RID) -o "$(OUT)/server"
	mv -f "$(OUT)/server/Z+ Server" "$(OUT)/server/zplus-server"
	chmod +x "$(OUT)/server/zplus-server"

client:
	$(DOTNET) publish src/ZPlus.ClientGui -c $(CONFIG) -f $(TFM) -r $(RID) $(SINGLE_FILE) -o "$(OUT)/client"
	chmod +x "$(OUT)/client/zplus-client"

admin:
	$(DOTNET) publish src/ZPlus.AdminGui -c $(CONFIG) -f $(TFM) -r $(RID) $(SINGLE_FILE) -o "$(OUT)/admin"
	chmod +x "$(OUT)/admin/zplus-admin"

clean:
	rm -rf "$(OUT)"
	$(DOTNET) clean -c $(CONFIG) --nologo -v q
