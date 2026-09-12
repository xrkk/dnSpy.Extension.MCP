class InboundStrong
{
    static int Main()
    {
        return typeof(StrongHost).FullName.Length + StrongHost.Shared();
    }
}
