// P07 ACC-015 fixture: a net48 module whose metadata carries an inbound
// AssemblyRef to ImportHost (built with /r:ImportHost.exe on the VM).  It is
// never executed — only loaded so the impact scan can observe the reference.
class InboundRef
{
    static void Main()
    {
        System.Console.WriteLine(typeof(ImportHost.Machines).FullName);
    }
}
