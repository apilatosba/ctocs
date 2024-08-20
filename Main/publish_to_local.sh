#/bin/bash

dotnet publish
PUBLISH_DIR="bin/Release/net8.0/publish/"
LOCAL_BIN_DIR="$HOME/bin"

# Copy the contents of the publish directory to the local bin directory
cp -r "$PUBLISH_DIR/"* "$LOCAL_BIN_DIR"
