using PharmaLIMS.Services;

int passed=0;
void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS "+name); }
void Expect(bool ok) { if(!ok) throw new Exception("Assertion failed"); }
Check("busy transition preserves session and forms", () => {
 var c=new SessionTransitionCoordinator(); bool touched=false;
 Expect(!c.TryTransition(true,()=>touched=true,()=>touched=true)); Expect(!touched);
});
Check("authority is cleared before replacement login is exposed", () => {
 var c=new SessionTransitionCoordinator(); var order=new List<string>();
 Expect(c.TryTransition(false,()=>order.Add("clear"),()=>order.Add("login")));
 Expect(string.Join(",",order)=="clear,login");
});
Check("reentry cannot clear or open a second session", () => {
 var c=new SessionTransitionCoordinator(); int clears=0, opens=0;
 Expect(c.TryTransition(false,()=> { clears++; Expect(!c.TryTransition(false,()=>clears++,()=>opens++)); },()=>opens++));
 Expect(clears==1 && opens==1);
});
Check("clear failure never exposes login", () => {
 var c=new SessionTransitionCoordinator(); bool opened=false;
 try { c.TryTransition(false,()=>throw new InvalidOperationException("clear"),()=>opened=true); throw new Exception("Did not throw"); }
 catch(InvalidOperationException) { Expect(!opened); }
 Expect(c.TryTransition(false,()=>{},()=>{}));
});
Check("login failure does not roll back logout", () => {
 var c=new SessionTransitionCoordinator(); bool authenticated=true;
 try { c.TryTransition(false,()=>authenticated=false,()=>throw new InvalidOperationException("login")); throw new Exception("Did not throw"); }
 catch(InvalidOperationException) { Expect(!authenticated); }
});
Check("target environment uses target profile and drops inherited overrides", () => {
 var env=new Dictionary<string,string?> { ["PHARMALIMS_CONNECTION_STRING"]="secret", ["PHARMALIMS_DPAPI_PROTECTED_PASSWORD"]="secret", ["PHARMALIMS_APPLY_STARTUP_DATABASE_UPDATES"]="true", ["USERPROFILE"]="old", ["PATH"]="preserved" };
 SwitchedProcessEnvironment.Apply(env,@"C:\Users\Reviewer");
 Expect(env["USERPROFILE"]==@"C:\Users\Reviewer");
 Expect(env["LOCALAPPDATA"]==@"C:\Users\Reviewer\AppData\Local");
 Expect(env["APPDATA"]==@"C:\Users\Reviewer\AppData\Roaming");
 Expect(env["TEMP"]==env["TMP"] && env["TEMP"]==@"C:\Users\Reviewer\AppData\Local\Temp");
 Expect(env["PATH"]=="preserved");
 Expect(!env.Keys.Any(k=>k.StartsWith("PHARMALIMS_")));
});
Check("blank profile cannot retain previous actor paths", () => {
 try { SwitchedProcessEnvironment.Apply(new Dictionary<string,string?>()," "); throw new Exception("Did not throw"); }
 catch(ArgumentException) { }
});
Check("switch cannot change the SQL target when overrides are removed", () => {
 SwitchedProcessEnvironment.EnsureTarget("tcp:AMEEN,1433", "EQ001_UAT_AMEEN_20261024", "tcp:AMEEN,1433", "EQ001_UAT_AMEEN_20261024");
 try { SwitchedProcessEnvironment.EnsureTarget("tcp:AMEEN,1433", "EQ001_UAT_AMEEN_20261024", "tcp:AMEEN,1433", "Production"); throw new Exception("Did not throw"); }
 catch(InvalidOperationException) { }
 try { SwitchedProcessEnvironment.EnsureTarget("tcp:AMEEN,1433", "EQ001_UAT_AMEEN_20261024", "other-server", "EQ001_UAT_AMEEN_20261024"); throw new Exception("Did not throw"); }
 catch(InvalidOperationException) { }
});
Check("visible preparing login cannot close the previous locked window", () => {
 Expect(!SessionTransitionCoordinator.CanFinishHandoff(false,true,SessionTransitionCoordinator.LoginTitle(true)));
 Expect(!SessionTransitionCoordinator.CanFinishHandoff(true,true,SessionTransitionCoordinator.LoginTitle(false)));
 Expect(!SessionTransitionCoordinator.CanFinishHandoff(false,false,SessionTransitionCoordinator.LoginTitle(false)));
 Expect(SessionTransitionCoordinator.CanFinishHandoff(false,true,SessionTransitionCoordinator.LoginTitle(false)));
});
Check("effective SQL connection overrides take precedence over descriptive settings", () => {
 var parent=SwitchedProcessEnvironment.ReadTarget("Data Source=tcp:AMEEN,1433;Initial Catalog=EQ001_UAT_AMEEN_20261024;Integrated Security=true");
 var child=SwitchedProcessEnvironment.ReadTarget("Server=tcp:AMEEN,1433;Database=Production;Integrated Security=true");
 Expect(parent.Server=="tcp:AMEEN,1433" && parent.Database=="EQ001_UAT_AMEEN_20261024");
 try { SwitchedProcessEnvironment.EnsureTarget(parent.Server,parent.Database,child.Server,child.Database); throw new Exception("Did not throw"); }
 catch(InvalidOperationException) { }
 try { SwitchedProcessEnvironment.ReadTarget("Server=tcp:AMEEN,1433;Integrated Security=true"); throw new Exception("Did not throw"); }
 catch(InvalidOperationException) { }
});
Console.WriteLine($"Session regression: {passed} checks passed.");
