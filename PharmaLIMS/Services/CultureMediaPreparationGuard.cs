using System.Data;
namespace PharmaLIMS.Services;
internal static class CultureMediaPreparationGuard
{
    internal static void EnsureAmendable(byte[]? loadedVersion,byte[] lockedVersion,bool hasSignedEvidence)
    {
        if(hasSignedEvidence) throw new InvalidOperationException("Signed visual or sterility evidence freezes preparation content. Use controlled QA correction.");
        if(loadedVersion==null || loadedVersion.Length!=8 || !loadedVersion.SequenceEqual(lockedVersion))
            throw new DBConcurrencyException("The preparation changed since it was loaded. Reload before amending it.");
    }
}
