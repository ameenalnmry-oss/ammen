param(
    [Parameter(Mandatory=$true)]
    [SecureString]$Password
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$entropy = [Text.Encoding]::UTF8.GetBytes('PharmaLIMS.DatabaseCredential.v1')
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    $bytes = [Text.Encoding]::UTF8.GetBytes($plain)
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect(
            $bytes,
            $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [Convert]::ToBase64String($protected)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $plain = $null
    }
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}
