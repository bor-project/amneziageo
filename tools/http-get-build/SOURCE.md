# HTTP GET runtime

The executable is built from this repository with Go1.27.3. Platform build-http-get scripts prepare the binary and a receipt containing normalized source and binary SHA256, target OS and architecture. MSBuild verifies the receipt and executable architecture before packaging.
