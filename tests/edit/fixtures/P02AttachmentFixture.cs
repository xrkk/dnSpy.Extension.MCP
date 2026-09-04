using System;
using System.Runtime.InteropServices;
using System.Security.Permissions;

namespace P02AttachmentFixture {
	[AttributeUsage(AttributeTargets.Class)]
	internal sealed class AttachedAttribute : Attribute {
		public AttachedAttribute() { }
	}

	[Attached]
	internal sealed class AttributeConsumer { }

	internal interface IAttached { }
	internal sealed class InterfaceAttachment : IAttached { }

	internal class ConstraintAttachment { }

	internal sealed class AttachmentTargets {
		// Forces a FieldRVA row in <PrivateImplementationDetails>.
		internal static readonly byte[] RvaFixture = { 1, 3, 5, 7, 9, 11, 13, 15, 17 };

		[DllImport("kernel32.dll")]
		internal static extern bool Beep(uint frequency, uint duration);

		[MarshalAs(UnmanagedType.I4)]
		internal int MarshaledField;

		internal void MarshaledParameter([MarshalAs(UnmanagedType.I4)] int value) { }

		[PrincipalPermission(SecurityAction.Demand, Role = "Administrators")]
		internal void SecuredMethod() { }

		internal void ConstrainedMethod<T>() where T : ConstraintAttachment { }

		internal int SemanticProperty { get; set; }
		internal event Action SemanticEvent;
	}
}
