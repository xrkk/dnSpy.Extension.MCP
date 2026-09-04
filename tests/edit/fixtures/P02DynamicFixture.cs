using System;

namespace P02Fixture {
	public class EditTargets<TExisting> {
		private sealed class RemovableType { }

		public int UpdateField;
		private int RemoveField;
		public string UpdateProperty { get; set; }
		private string RemoveProperty { get; set; }
		public event Action UpdateEvent;
		private event Action RemoveEvent;

		public void UpdateMethod() { }
		private void RemoveMethod() { }
		public void ParameterAddTarget() { }
		public void ParameterUpdateTarget(int value) { }
		private void ParameterRemoveTarget(int value) { }
		public void GenericAddTarget() { }
		public void GenericUpdateTarget<TUpdate>() { }
		private void GenericRemoveTarget<TUnused>() { }
		public void BodyTarget() { }
		public void AddEventAccessor(Action value) { }
		public void RemoveEventAccessor(Action value) { }
	}

	public static class Program {
		public static int Compute(int value) => value * 2 + 1;

		public static int Main(string[] args) {
			Console.WriteLine("p02-dynamic:" + args.Length);
			return Compute(args.Length) >= 1 ? 0 : 1;
		}
	}
}
