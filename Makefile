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
RID    ?= linux-x64
TFM    ?= net10.0
OUT    ?= publish/linux
DOTNET ?= dotnet

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
