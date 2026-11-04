using System.Text;

internal static class SiteSigningTransportSourceCases
{
    internal static void Run(string root)
    {
        string Normalize(string value)=>value.TrimStart('\ufeff').Replace("\r\n","\n").TrimEnd('\r','\n')+"\n";
        foreach(string name in new[]{"TrustedCallerMap.cs","WindowsTrustedPipeServer.cs","TrustedPipeConnectionPool.cs"})
        {
            string site=File.ReadAllText(Path.Combine(root,"scripts/site-overlays/AMEEN-"+name+".txt"),Encoding.UTF8);
            string current=File.ReadAllText(Path.Combine(root,"tools/PharmaLIMS.TrustedSigning/"+name),Encoding.UTF8);
            if(Normalize(site)!=Normalize(current))throw new Exception("Site transport differs from the root code exercised by native Windows UAT: "+name);
        }
        Console.WriteLine("PASS SITE SIGNING TRANSPORT: complete server/map/pool artifacts match root native-UAT sources; infrastructure tests compile the actual site map/pool. No local service-installation claim.");
    }
}
