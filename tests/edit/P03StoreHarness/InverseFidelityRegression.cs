using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// Exercises the actual compiled inverse, not a substitute snapshot codec.
// Keep this red until the production inverse preserves all captured semantics.
internal static class InverseFidelityRegression {
	sealed class CaseContext {
		public ModuleDef Module { get; }
		public Dictionary<string, IMDTokenProvider> Objects { get; }
		public byte[] Baseline { get; set; }
		public Action Verify { get; set; }
		public string Kind { get; set; } = "";
		public IMDTokenProvider? Subject { get; set; }
		public object? Changes { get; set; }
		public CaseContext(ModuleDef module, Dictionary<string, IMDTokenProvider> objects, byte[] baseline) {
			Module = module; Objects = objects; Baseline = baseline; Verify = () => { };
		}
	}

	public static void Run(string fixture) {
		var failures = 0;
		// Kind-aware case runner: capture the subject's original image bytes,
		// apply the compiled inverse after the forward operation, then verify
		// exact restoration through per-case checks.
		void Case(string name, Action<CaseContext> body) {
			using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var objects = new Dictionary<string, IMDTokenProvider>();
			var context = new CaseContext(module, objects, Array.Empty<byte>());
			var stage = "setup";
			try {
				body(context);
				// Baseline is captured after the case materializes its subject so the
				// comparison covers the restored state, not the pristine module.
				context.Baseline = EditWorkspace.WriteCanonical(module);
				if (context.Subject != null) objects["subject"] = context.Subject;
				var operation = new Dictionary<string, object?> {
					["kind"] = context.Kind, ["target"] = new { object_id = "subject" },
				};
				foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(context.Changes))!)
					operation[pair.Key] = pair.Value;
				using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation));
				stage = "compile";
				var compiled = EditOperationRegistry.CompileInverse(module, forward.RootElement, objects);
				stage = "serialize";
				using var inverse = JsonDocument.Parse(JsonSerializer.Serialize(compiled, EditWire.JsonOptions));
				stage = "forward";
				EditOperationRegistry.Apply(module, forward.RootElement, objects, 0);
				stage = "inverse";
				EditOperationRegistry.ApplyCompiledInverse(module, inverse.RootElement, objects, 0);
				stage = "compare";
				if (!ReferenceEquals(objects["subject"], context.Subject))
					throw new InvalidOperationException("Subject identity changed");
				context.Verify();
				if (!context.Baseline.SequenceEqual(EditWorkspace.WriteCanonical(module))) throw new InvalidOperationException("Restored image bytes changed");
				Console.WriteLine("PASS compiled-inverse-fidelity " + name);
			}
			catch (Exception ex) {
				failures++;
				Console.WriteLine("FAIL compiled-inverse-fidelity " + name + " stage=" + stage + " error=" + ex.GetType().Name + ": " + ex.Message);
			}
		}
		void FieldCase(string name, Func<ModuleDef, FieldDef> create, object changes) =>
			Case(name, ctx => {
				var owner = ctx.Module.GetTypes().Single(t => t.FullName == "TestIL.Members");
				var field = create(ctx.Module);
				owner.Fields.Add(field);
				var originalName = (byte[])field.Name.Data.Clone();
				var originalType = field.FieldType;
				var originalConstant = field.Constant;
				ctx.Kind = "field_update"; ctx.Subject = field; ctx.Changes = changes;
				ctx.Verify = () => {
					if (!field.Name.Data.SequenceEqual(originalName)) throw new InvalidOperationException("Name UTF8 bytes changed");
					if (!new SigComparer().Equals(originalType, field.FieldType)) throw new InvalidOperationException("Field signature changed");
					if (originalConstant?.Value is float expected &&
						(field.Constant?.Value is not float actual || BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual)))
						throw new InvalidOperationException("Float constant bits changed");
				};
			});
		FieldCase("ordinary-name-control", m => new FieldDefUser("Original", new FieldSig(m.CorLibTypes.Int32), FieldAttributes.Public), new { name = "Changed" });
		FieldCase("complex-signature", m => new FieldDefUser("Complex", new FieldSig(new CModReqdSig(
			new TypeRefUser(m, "System.Runtime.CompilerServices", "IsVolatile", m.CorLibTypes.AssemblyRef),
			new ArraySig(m.CorLibTypes.Int32, 2, new uint[] { 3 }, new[] { -1 }))), FieldAttributes.Public), new { field_type = "System.Int32" });
		FieldCase("raw-name-bytes", m => new FieldDefUser(new UTF8String(new byte[] { 0x4e, 0xff, 0xfe }),
			new FieldSig(m.CorLibTypes.Int32), FieldAttributes.Public), new { name = "Changed" });
		FieldCase("nan-constant", m => new FieldDefUser("Nan", new FieldSig(m.CorLibTypes.Single),
			FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault) {
			Constant = new ConstantUser(BitConverter.Int32BitsToSingle(unchecked((int)0x7fc12345))),
		}, new { clear_constant = true });
		Case("type-update", ctx => {
			var owner = ctx.Module.GetTypes().Single(t => t.FullName == "TestIL.Members");
			var nested = new TypeDefUser(null, new UTF8String(new byte[] { 0x52, 0xfe }), owner) { Attributes = TypeAttributes.NestedPublic | TypeAttributes.Sealed };
			var originalName = (byte[])nested.Name.Data.Clone();
			var originalBase = nested.BaseType;
			owner.NestedTypes.Add(nested);
			ctx.Kind = "type_update"; ctx.Subject = nested; ctx.Changes = new { name = "Changed", base_type = "System.Object" };
			ctx.Verify = () => {
				if (!nested.Name.Data.SequenceEqual(originalName)) throw new InvalidOperationException("Type name UTF8 bytes changed");
				if (!new SigComparer().Equals(originalBase?.ToTypeSig(), nested.BaseType?.ToTypeSig())) throw new InvalidOperationException("Type base changed");
			};
		});
		Case("method-update", ctx => {
			var owner = ctx.Module.GetTypes().Single(t => t.FullName == "TestIL.Members");
			var method = new MethodDefUser(new UTF8String(new byte[] { 0x4d, 0xfd }), MethodSig.CreateInstance(new CModReqdSig(
				new TypeRefUser(ctx.Module, "System.Runtime.CompilerServices", "IsVolatile", ctx.Module.CorLibTypes.AssemblyRef),
				new ArraySig(ctx.Module.CorLibTypes.Int32, 2, new uint[] { 3 }, new[] { -1 }))),
				MethodAttributes.Public | MethodAttributes.Static);
			var originalName = (byte[])method.Name.Data.Clone();
			var originalReturn = method.ReturnType;
			owner.Methods.Add(method);
			ctx.Kind = "method_update"; ctx.Subject = method; ctx.Changes = new { name = "Renamed", return_type = "System.Int32", has_this = false };
			ctx.Verify = () => {
				if (!method.Name.Data.SequenceEqual(originalName)) throw new InvalidOperationException("Method name UTF8 bytes changed");
				if (!new SigComparer().Equals(originalReturn, method.ReturnType)) throw new InvalidOperationException("Method return changed");
			};
		});
		Case("property-update", ctx => {
			var owner = ctx.Module.GetTypes().Single(t => t.FullName == "TestIL.Members");
			var getter = new MethodDefUser("get_RawProp", MethodSig.CreateInstance(ctx.Module.CorLibTypes.Int32),
				MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName);
			owner.Methods.Add(getter);
			var property = new PropertyDefUser("RawBytes", new PropertySig(true, new CModReqdSig(
				new TypeRefUser(ctx.Module, "System.Runtime.CompilerServices", "IsVolatile", ctx.Module.CorLibTypes.AssemblyRef),
				ctx.Module.CorLibTypes.Int32), Array.Empty<TypeSig>()), (PropertyAttributes)0) { GetMethod = getter };
			var originalName = (byte[])property.Name.Data.Clone();
			var originalSignature = property.PropertySig;
			var originalGetter = property.GetMethod;
			owner.Properties.Add(property);
			ctx.Kind = "property_update"; ctx.Subject = property; ctx.Changes = new { property_type = "System.String", getter = (object?)null };
			ctx.Verify = () => {
				if (!property.Name.Data.SequenceEqual(originalName)) throw new InvalidOperationException("Property name UTF8 bytes changed");
				if (!new SigComparer().Equals(originalSignature.RetType, property.PropertySig.RetType)) throw new InvalidOperationException("Property signature changed");
				if (!ReferenceEquals(originalGetter, property.GetMethod)) throw new InvalidOperationException("Property getter changed");
			};
		});
		Case("event-update", ctx => {
			var owner = ctx.Module.GetTypes().Single(t => t.FullName == "TestIL.Members");
			var add = new MethodDefUser("add_RawEvt", MethodSig.CreateInstance(ctx.Module.CorLibTypes.Void),
				MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName);
			owner.Methods.Add(add);
			var evt = new EventDefUser(new UTF8String(new byte[] { 0x45, 0xfc }), ctx.Module.CorLibTypes.Object.TypeDefOrRef, (EventAttributes)0) { AddMethod = add };
			var originalName = (byte[])evt.Name.Data.Clone();
			var originalType = evt.EventType;
			owner.Events.Add(evt);
			ctx.Objects["evt-add"] = add;
			ctx.Kind = "event_update"; ctx.Subject = evt; ctx.Changes = new { event_type = "System.String", remove_method = new { object_id = "evt-add" } };
			ctx.Verify = () => {
				if (!evt.Name.Data.SequenceEqual(originalName)) throw new InvalidOperationException("Event name UTF8 bytes changed");
				if (!new SigComparer().Equals(originalType?.ToTypeSig(), evt.EventType?.ToTypeSig())) throw new InvalidOperationException("Event type changed");
			};
		});
		Case("parameter-update", ctx => {
			var method = ctx.Module.GetTypes().Single(t => t.FullName == "TestIL.UnityComponent").Methods.Single(m => m.Name == "OnTriggerEnter");
			var param = method.ParamDefs.Single(p => p.Sequence == 1);
			var originalName = (byte[])param.Name.Data.Clone();
			var originalType = method.MethodSig.Params[0];
			ctx.Kind = "parameter_update"; ctx.Subject = param; ctx.Changes = new {
				parameter_target = new { owner_method = new { token = "0x" + method.MDToken.Raw.ToString("x8") }, parameter_index = 0 },
				name = "renamed", parameter_type = "System.Int32",
			};
			ctx.Verify = () => {
				if (!param.Name.Data.SequenceEqual(originalName)) throw new InvalidOperationException("Parameter name UTF8 bytes changed");
				if (!new SigComparer().Equals(originalType, method.MethodSig.Params[0])) throw new InvalidOperationException("Parameter signature changed");
			};
		});
		Case("generic-update", ctx => {
			var genericOwner = ctx.Module.GetTypes().Single(t => t.Name == "GenericFieldOwner`1");
			var generic = new GenericParamUser(1, (GenericParamAttributes)0, new UTF8String(new byte[] { 0x54, 0xfb }));
			genericOwner.GenericParameters.Add(generic);
			var originalName = (byte[])generic.Name.Data.Clone();
			ctx.Kind = "generic_parameter_update"; ctx.Subject = generic; ctx.Changes = new { name = "TNew" };
			ctx.Verify = () => {
				if (!generic.Name.Data.SequenceEqual(originalName)) throw new InvalidOperationException("Generic name UTF8 bytes changed");
			};
		});
		if (failures != 0) throw new InvalidOperationException("Compiled inverse fidelity failures=" + failures + "/10");
	}
}
