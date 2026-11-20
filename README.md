<div align="center">

![APItoolkit's Logo](https://github.com/apitoolkit/.github/blob/main/images/logo-white.svg?raw=true#gh-dark-mode-only)
![APItoolkit's Logo](https://github.com/apitoolkit/.github/blob/main/images/logo-black.svg?raw=true#gh-light-mode-only)

## .NET Core SDK

[![APItoolkit SDK](https://img.shields.io/badge/APItoolkit-SDK-0068ff?logo=dotnet)](https://github.com/topics/apitoolkit-sdk) [![Join Discord Server](https://img.shields.io/badge/Chat-Discord-7289da)](https://apitoolkit.io/discord?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme) [![APItoolkit Docs](https://img.shields.io/badge/Read-Docs-0068ff)](https://apitoolkit.io/docs/sdks/dotnet/dotnetcore?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme) [![Build Status](https://github.com/apitoolkit/apitoolkit-dotnet/workflows/.NET/badge.svg)](https://github.com/apitoolkit/apitoolkit-dotnet1/actions?query=workflow%3ACI) [![NuGet](https://img.shields.io/nuget/v/ApiToolkit.Net.svg)](https://nuget.org/packages/ApiToolkit.Net) [![Nuget](https://img.shields.io/nuget/dt/ApiToolkit.Net.svg)](https://nuget.org/packages/ApiToolkit.Net)

APIToolkit .NET SDK is a middleware that can be used to monitor HTTP requests. It is provides additional functionalities on top of the open telemetry instrumentation which creates a custom span for each request capturing details about the request including request, response bodies errors and outgoing requests.

</div>

---

## Table of Contents

- [Installation](#installation)
- [Setup OpenTelemetry](#setup-opentelemetry)
- [APItoolkit SDK Configuration](#sdk-configuration)
- [Contributing and Help](#contributing-and-help)
- [License](#license)

---

## Installation

Kindly run the command below to install the package:

```sh
dotnet add package ApiToolkit.Net
```

## Setup OpenTelemetry

Run the following command to install the OpenTelemetry auto instrumentation for .NET:

```sh
# Download the bash script
curl -sSfL https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/latest/download/otel-dotnet-auto-install.sh -O

# Install core files
sh ./otel-dotnet-auto-install.sh

# Enable execution for the instrumentation script
chmod +x $HOME/.otel-dotnet-auto/instrument.sh

# Setup the instrumentation for the current shell session
. $HOME/.otel-dotnet-auto/instrument.sh
```

#### OpenTelemetry Configuration

After installating .NET autoinstrumentation packages, you can configure the OpenTelemetry instrumentation by setting the following environment variables:

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT="https://otel.monoscopehq.com" # Specifies the endpoint to send the traces to.
export OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES="APItoolkit.HTTPInstrumentation" # The apitoolkit instrumentation  activity resource.
export OTEL_SERVICE_NAME="my-service" # Specifies the name of the service.
export OTEL_RESOURCE_ATTRIBUTES="at-project-key={ENTER_YOUR_API_KEY_HERE}" # Adds your API KEY to the resource.
export OTEL_EXPORTER_OTLP_PROTOCOL="grpc" # Specifies the protocol to use for the OpenTelemetry exporter.
```

After setting the environment variables, build and run your application and you should see the logs, traces and metrics in the APIToolkit dashboard.

## SDK Configuration

The SDK allows you to capture the request and response bodies, errors, outgoing requests and more.

eg:

```csharp
using ApiToolkit.Net;

// Initialize the APItoolkit client
var config = new Config
{
    Debug = false,
    Tags = new List<string> { "environment: production", "region: us-east-1" },
    ServiceVersion: "v2.0",
};
var client = await APIToolkit.NewClient(config);
// END Initialize the APItoolkit client
// Register the middleware to use the initialized client
app.Use(async (context, next) =>
{
    var apiToolkit = new APIToolkit(next, client);
    await apiToolkit.InvokeAsync(context);
});

# app.UseEndpoint(..)
# other middleware and logic
# ...
```

> [!NOTE]
>
> - Please make sure the APItoolkit middleware is added before `UseEndpoint` and other middleware are initialized.
> - The `{ENTER_YOUR_API_KEY_HERE}` demo string should be replaced with the [API key](https://apitoolkit.io/docs/dashboard/settings-pages/api-keys?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme) generated from the APItoolkit dashboard.

<br />

> [!IMPORTANT]
>
> To learn more configuration options (redacting fields, error reporting, outgoing requests, etc.), please read this [SDK documentation](https://apitoolkit.io/docs/sdks/dotnet/dotnetcore?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme).

## Contributing and Help

To contribute to the development of this SDK or request help from the community and our team, kindly do any of the following:

- Read our [Contributors Guide](https://github.com/apitoolkit/.github/blob/main/CONTRIBUTING.md).
- Join our community [Discord Server](https://apitoolkit.io/discord?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme).
- Create a [new issue](https://github.com/apitoolkit/apitoolkit-dotnet/issues/new/choose) in this repository.

## License

This repository is published under the [MIT](LICENSE) license.

---

<div align="center">

<a href="https://apitoolkit.io?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme" target="_blank" rel="noopener noreferrer"><img src="https://github.com/apitoolkit/.github/blob/main/images/icon.png?raw=true" width="40" /></a>

</div>
