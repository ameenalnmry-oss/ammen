using System.Runtime.CompilerServices;
// The host reuses existing guarded authentication/account transitions. Opaque
// rowversions remain internal to the application and trusted host assemblies.
[assembly: InternalsVisibleTo("PharmaLIMS.TrustedSigning.Host")]
