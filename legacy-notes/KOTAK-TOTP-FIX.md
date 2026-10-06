# Kotak Neo TOTP generation fix

Kotak Neo authentication now uses the same OtpNet approach as the existing project.

```csharp
byte[] secretBytes = Base32Encoding.ToBytes(_options.TotpSecret.Trim());
var totp = new Totp(secretBytes);
string totpCode = totp.ComputeTotp();
```

`TotpSecret` remains the Base32 seed. It is never replaced with the generated 6-digit OTP.

The manual Base32 decoder / HMAC-SHA1 implementation was removed so the project has only one TOTP generation path.

NuGet dependency added:

```xml
<PackageReference Include="Otp.NET" Version="1.4.1" />
```
