namespace T004Plain
{
    // The zero-async-surface host: no Task, no async/iterator rows anywhere in
    // this assembly, so a first async add must synthesize the whole reference
    // surface (assembly/type/member/spec rows) through reference_add.
    public class Simple
    {
        public static int Baseline() => 1;
    }
}
