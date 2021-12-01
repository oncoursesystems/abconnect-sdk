![Instructure logo](https://raw.githubusercontent.com/oncoursesystems/abconnect-sdk/master/instructure.png)

# OnCourse.ABConnect

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![Build Status](https://github.com/oncoursesystems/abconnect-sdk/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/oncoursesystems/abconnect-sdk/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/OnCourse.ABConnect)](https://www.nuget.org/packages/OnCourse.ABConnect/)

### OnCourse.ABConnect is a .NET SDK library used to communicate with the [Academic Benchmarks AB Connect API](https://abconnect.docs.apiary.io/)

## ✔ Features

Academic Benchmarks API library helps to generate requests for following services:

 * authorities
 * publications
 * documents
 * standards
 * regions
 * events

 ## ⭐ Installation

 This project is a class library built for compatibility all the back to .NET 6.0.

To install the OnCourse.ABConnect NuGet package, run the following command via the dotnet CLI

```
dotnet add package OnCourse.ABConnect
```

Or run the following command in the Package Manager Console of Visual Studio

```
PM> Install-Package OnCourse.ABConnect
```

## 📕 General Usage
### Initialization
To use ABConnect, import the namespace and include the .UseABConnect() method when initializing the host builder (typically
found in the Program.cs file)

```csharp
using OnCourse.ABConnect;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        services.AddABConnect(hostContext.Configuration);
    });
```

### Fault Handling / Resilience

By default, the client will be configured to retry a call up to three times with increasing waits between (1s, 5s, 10s).  If after the third call the service still returns an error then the call will be considered failed.  You can override this policy during the UseABConnect method by passing in a policy as the second parameter.  It is recommended to use [Polly](https://github.com/App-vNext/Polly), a 3rd-party library, that has a lot of options for creating policies

```csharp
services.UseABConnect(configuration, (p => p.WaitAndRetryAsync(new[]
{
    TimeSpan.FromSeconds(1),
    TimeSpan.FromSeconds(5),
    TimeSpan.FromSeconds(10)
}));
```

### Configuration

To use the API, you must add the Partner ID and Key in the appSettings.json file under the *ABConnect* section:

```json
{
    "ABConnect": {
        "PartnerId": <YOUR_ID>,
        "PartnerKey": <YOUR_KEY>
    }
}
