using System.Collections.Generic;
public class CdiHost { public static IEnumerable<int> Values(int n) { int x=n+1; yield return x; x+=n; yield return x; } }
