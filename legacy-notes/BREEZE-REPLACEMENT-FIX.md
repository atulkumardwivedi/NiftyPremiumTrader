# Breeze replacement repair

Replace the entire api/Services/BreezeMarketDataClient.cs file; do not append it to the old file. Keep only one copy of the class under the API project.

This project targets .NET 10. Run dotnet build api/NiftyPremiumTrader.Api.csproj using the .NET 10 SDK.

The replacement retains the original namespace, imports, constructor, and all public method signatures. All helper methods remain within BreezeMarketDataClient.

Historical V2 request clocks use 09:15 through 15:35 (literal .000Z format), with output filtered to 09:15 through 15:30 India time. Offset-less historical response labels are normalized from India time to UTC; explicit response offsets are respected. Live request clocks are converted to India time and completed-candle filtering is retained.

Reference: https://github.com/Idirect-Tech/Breeze-Python-SDK#histroical-data-v2--options

Validation: All 32 API C# source files passed a tree-sitter C# syntax parse. The replacement has no top-level statements and all 14 methods are within its class. A full dotnet build and authenticated Breeze download could not be run in this environment. No claim of build or live API verification is made.
