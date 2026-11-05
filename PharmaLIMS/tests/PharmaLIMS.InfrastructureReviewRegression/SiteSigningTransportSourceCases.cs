using System.Text;

internal static class SiteSigningTransportSourceCases
{
    internal static void Run(string root)
    {
        string Normalize(string value)=>value.TrimStart('\ufeff').Replace("\r\n","\n").TrimEnd('\r','\n')+"\n";
        foreach(string name in new[]{"TrustedCallerMap.cs","WindowsTrustedPipeServer.cs","TrustedPipeConnectionPool.cs"})
        {
            // The frozen 306 site artifacts remain unchanged for their original
            // cohort. The active 309 server additionally carries canonical activity
            // authentication/snapshots and diagnostics, so bind it to its reviewed
            // complete R9 merge artifact rather than relabelling the old evidence.
            string artifact=name=="WindowsTrustedPipeServer.cs"
                ? "R9-AMEEN-WindowsTrustedPipeServer.cs.txt" : "AMEEN-"+name+".txt";
            string site=File.ReadAllText(Path.Combine(root,"scripts/site-overlays/"+artifact),Encoding.UTF8);
            string current=File.ReadAllText(Path.Combine(root,"tools/PharmaLIMS.TrustedSigning/"+name),Encoding.UTF8);
            if(Normalize(site)!=Normalize(current))throw new Exception("Site transport differs from the root code exercised by native Windows UAT: "+name);
        }
        Console.WriteLine("PASS SITE SIGNING TRANSPORT: reviewed R9 server and retained map/pool artifacts match active native-UAT sources; frozen 306 evidence remains separate. No local service-installation claim.");
    }
}
