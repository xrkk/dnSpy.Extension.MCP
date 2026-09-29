// Scenario sample (F07): rename-friendly symbol tree.
using System;

namespace RenameTree.Sample {
    public interface IRepository { object Fetch(int id); }
    public class OldRepository : IRepository {
        public object Fetch(int id) { return "row-" + id; }
        public void LegacyFlush() { }
    }
    public class OldService {
        OldRepository repo = new OldRepository();
        public object GetData(int id) { return repo.Fetch(id); }
        public string OldLabel { get; set; }
        public event EventHandler OldChanged;
        public void Fire() { if (OldChanged != null) OldChanged(this, EventArgs.Empty); }
    }
    public class UglyName_1 { public class UglyName_2 { public void DoThing() { } } }
}
