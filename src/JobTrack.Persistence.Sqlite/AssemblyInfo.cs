using System.Runtime.InteropServices;

// Framework Design Guidelines ch. 4 ("Assemblies"): any assembly with public types declares CLS
// compliance and COM visibility. CLSCompliant is the compiler-enforced check that this library's
// public surface is consumable from other CLR languages, which is otherwise only an aspiration.
// Members that cannot satisfy it -- because a dependency's own types are not marked compliant --
// carry their own [CLSCompliant(false)] rather than the assembly abandoning the claim.
[assembly: CLSCompliant(true)]

// Nothing here is designed for COM interop.
[assembly: ComVisible(false)]
