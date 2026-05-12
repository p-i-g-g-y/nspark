# Third-Party Notices

NSpark depends on the following third-party components. The full text of
each license is available at the linked source.

## Managed (.NET) dependencies

| Package | License | Source |
|---|---|---|
| `Google.Protobuf` | BSD-3-Clause | <https://github.com/protocolbuffers/protobuf/blob/main/LICENSE> |
| `Grpc.Net.Client`, `Grpc.Net.ClientFactory`, `Grpc.Tools` | Apache-2.0 | <https://github.com/grpc/grpc-dotnet/blob/master/LICENSE> |
| `NBitcoin` | MIT | <https://github.com/MetacoSA/NBitcoin/blob/master/LICENSE> |
| `Microsoft.Extensions.*` (DI, Options, Http, Logging, Hosting) | MIT | <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> |
| `Polly` (8.x) | BSD-3-Clause | <https://github.com/App-vNext/Polly/blob/main/LICENSE.txt> |
| `Microsoft.SourceLink.GitHub` | MIT | <https://github.com/dotnet/sourcelink/blob/main/License.txt> |
| `Microsoft.CodeAnalysis.*Analyzers` | MIT | <https://github.com/dotnet/roslyn-analyzers/blob/main/License.txt> |
| `Roslynator.Analyzers` | Apache-2.0 | <https://github.com/dotnet/roslynator/blob/main/LICENSE.TXT> |
| `MinVer` | Apache-2.0 | <https://github.com/adamralph/minver/blob/main/LICENSE.md> |
| `BenchmarkDotNet` | MIT | <https://github.com/dotnet/BenchmarkDotNet/blob/master/LICENSE.md> |
| `NUnit`, `NUnit3TestAdapter`, `NUnit.Analyzers` | MIT | <https://github.com/nunit/nunit/blob/main/LICENSE.txt> |
| `coverlet.collector` | MIT | <https://github.com/coverlet-coverage/coverlet/blob/master/LICENSE> |
| `FluentAssertions` | Apache-2.0 | <https://github.com/fluentassertions/fluentassertions/blob/develop/LICENSE> |

## Native dependencies

The `libspark_frost` native library bundled in
`src/NSpark/runtimes/<rid>/native/` is built from the
`spark_frost` Rust crate. The Rust crate and its transitive Cargo
dependencies carry their own licenses (predominantly MIT / Apache-2.0).

Per-release SBOMs include the exact crate names, versions, and license
identifiers; see the CycloneDX SBOM attached to each
[GitHub release](https://github.com/piggy/spark-csharp-sdk/releases).

## Notices

If you redistribute NSpark, you must satisfy the obligations of each of the
above licenses. None of the listed licenses imposes a copyleft requirement
on application code that merely consumes NSpark.

Apache-2.0 dependencies require carrying the `NOTICE` file, where present,
of the upstream project. A consolidated NOTICE will be added to the NuGet
content path `licenses/` before v1.0.
