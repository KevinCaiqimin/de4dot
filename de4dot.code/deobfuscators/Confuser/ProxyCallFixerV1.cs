﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.Confuser {
	class ProxyCallFixerV1 : ProxyCallFixer2 {
		MethodDefinitionAndDeclaringTypeDict<ProxyCreatorInfo> methodToInfo = new MethodDefinitionAndDeclaringTypeDict<ProxyCreatorInfo>();
		FieldDefinitionAndDeclaringTypeDict<List<MethodDefinition>> fieldToMethods = new FieldDefinitionAndDeclaringTypeDict<List<MethodDefinition>>();
		string ourAsm;
		ConfuserVersion version = ConfuserVersion.Unknown;

		enum ConfuserVersion {
			Unknown,
			v10_r42915,
			v10_r48717,
		}

		enum ProxyCreatorType {
			None,
			CallOrCallvirt,
			Newobj,
		}

		class ProxyCreatorInfo {
			public readonly MethodDefinition creatorMethod;
			public readonly ProxyCreatorType proxyCreatorType;
			public readonly ConfuserVersion version;

			public ProxyCreatorInfo(MethodDefinition creatorMethod, ProxyCreatorType proxyCreatorType, ConfuserVersion version) {
				this.creatorMethod = creatorMethod;
				this.proxyCreatorType = proxyCreatorType;
				this.version = version;
			}
		}

		class DelegateInitInfo {
			public readonly byte[] data;
			public readonly FieldDefinition field;
			public readonly MethodDefinition creatorMethod;

			public DelegateInitInfo(FieldDefinition field, MethodDefinition creatorMethod) {
				this.field = field;
				this.creatorMethod = creatorMethod;
			}

			public DelegateInitInfo(string data, FieldDefinition field, MethodDefinition creatorMethod) {
				this.data = Convert.FromBase64String(data);
				this.field = field;
				this.creatorMethod = creatorMethod;
			}
		}

		protected override bool ProxyCallIsObfuscated {
			get { return true; }
		}

		public IEnumerable<FieldDefinition> Fields {
			get {
				var fields = new List<FieldDefinition>(fieldToMethods.getKeys());
				var type = DotNetUtils.getModuleType(module);
				if (fields.Count > 0 && type != null) {
					foreach (var field in type.Fields) {
						var fieldType = field.FieldType as TypeDefinition;
						if (fieldType != null && delegateTypesDict.ContainsKey(fieldType))
							fields.Add(field);
					}
				}
				return fields;
			}
		}

		public override IEnumerable<Tuple<MethodDefinition, string>> OtherMethods {
			get {
				var list = new List<Tuple<MethodDefinition, string>>();
				foreach (var creatorMethod in methodToInfo.getKeys()) {
					list.Add(new Tuple<MethodDefinition, string> {
						Item1 = creatorMethod,
						Item2 = "Delegate creator method",
					});
				}
				foreach (var methods in fieldToMethods.getValues()) {
					foreach (var method in methods) {
						list.Add(new Tuple<MethodDefinition, string> {
							Item1 = method,
							Item2 = "Proxy delegate method",
						});
					}
				}
				return list;
			}
		}

		public ProxyCallFixerV1(ModuleDefinition module)
			: base(module) {
			ourAsm = (module.Assembly.Name ?? new AssemblyNameReference(" -1-1-1-1-1- ", new Version(1, 2, 3, 4))).FullName;
		}

		protected override object checkCctor(TypeDefinition type, MethodDefinition cctor) {
			throw new NotSupportedException();
		}

		protected override void getCallInfo(object context, FieldDefinition field, out MethodReference calledMethod, out OpCode callOpcode) {
			var info = (DelegateInitInfo)context;
			var creatorInfo = methodToInfo.find(info.creatorMethod);

			switch (creatorInfo.version) {
			case ConfuserVersion.v10_r42915:
				getCallInfo_v10_r42915(info, creatorInfo, out calledMethod, out callOpcode);
				break;

			case ConfuserVersion.v10_r48717:
				getCallInfo_v10_r48717(info, creatorInfo, out calledMethod, out callOpcode);
				break;

			default:
				throw new ApplicationException("Unknown version");
			}
		}

		void getCallInfo_v10_r42915(DelegateInitInfo info, ProxyCreatorInfo creatorInfo, out MethodReference calledMethod, out OpCode callOpcode) {
			var reader = new BinaryReader(new MemoryStream(info.data));

			bool isCallvirt = false;
			if (creatorInfo.proxyCreatorType == ProxyCreatorType.CallOrCallvirt)
				isCallvirt = reader.ReadBoolean();

			var asmRef = readAssemblyNameReference(reader);
			// If < 1.0 r42919, then high byte is 06, else it's cleared.
			uint token = (reader.ReadUInt32() & 0x00FFFFFF) | 0x06000000;
			if (reader.BaseStream.Position != reader.BaseStream.Length)
				throw new ApplicationException("Extra data");

			if (asmRef.FullName == ourAsm)
				calledMethod = (MethodReference)module.LookupToken((int)token);
			else
				calledMethod = createMethodReference(asmRef, token);

			callOpcode = getCallOpCode(creatorInfo, isCallvirt);
		}

		void getCallInfo_v10_r48717(DelegateInitInfo info, ProxyCreatorInfo creatorInfo, out MethodReference calledMethod, out OpCode callOpcode) {
			int offs = creatorInfo.proxyCreatorType == ProxyCreatorType.CallOrCallvirt ? 2 : 1;
			uint token = BitConverter.ToUInt32(Encoding.Unicode.GetBytes(info.field.Name.ToCharArray(), offs, 2), 0);
			if (info.field.Name[0] == (char)1)
				calledMethod = (MethodReference)module.LookupToken((int)token);
			else {
				var asmRef = module.AssemblyReferences[info.field.Name[0] - 2];
				calledMethod = createMethodReference(asmRef, token);
			}

			bool isCallvirt = false;
			if (creatorInfo.proxyCreatorType == ProxyCreatorType.CallOrCallvirt && info.field.Name[1] == '\r')
				isCallvirt = true;
			callOpcode = getCallOpCode(creatorInfo, isCallvirt);
		}

		// A method token is not a stable value so this method can fail to return the correct method!
		// There's nothing I can do about that. It's an obfuscator bug.
		MethodReference createMethodReference(AssemblyNameReference asmRef, uint methodToken) {
			var asm = AssemblyResolver.Instance.Resolve(asmRef);
			if (asm == null)
				return null;

			var method = asm.MainModule.LookupToken((int)methodToken) as MethodDefinition;
			if (method == null)
				return null;

			return module.Import(method);
		}

		static AssemblyNameReference readAssemblyNameReference(BinaryReader reader) {
			var name = readString(reader);
			var version = new Version(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
			var culture = readString(reader);
			byte[] pkt = reader.ReadBoolean() ? reader.ReadBytes(8) : null;
			return new AssemblyNameReference(name, version) {
				Culture = culture,
				PublicKeyToken = pkt,
			};
		}

		static string readString(BinaryReader reader) {
			int len = reader.ReadByte();
			var bytes = new byte[len];
			for (int i = 0; i < len; i++)
				bytes[i] = (byte)(reader.ReadByte() ^ len);
			return Encoding.UTF8.GetString(bytes);
		}

		static OpCode getCallOpCode(ProxyCreatorInfo info, bool isCallvirt) {
			switch (info.proxyCreatorType) {
			case ProxyCreatorType.Newobj:
				return OpCodes.Newobj;

			case ProxyCreatorType.CallOrCallvirt:
				return isCallvirt ? OpCodes.Callvirt : OpCodes.Call;

			default: throw new NotImplementedException();
			}
		}

		public void findDelegateCreator() {
			var type = DotNetUtils.getModuleType(module);
			foreach (var method in type.Methods) {
				if (method.Body == null || !method.IsStatic || !method.IsAssembly)
					continue;
				ConfuserVersion theVersion = ConfuserVersion.Unknown;

				if (DotNetUtils.isMethod(method, "System.Void", "(System.String,System.RuntimeFieldHandle)"))
					theVersion = ConfuserVersion.v10_r42915;
				else if (DotNetUtils.isMethod(method, "System.Void", "(System.RuntimeFieldHandle)"))
					theVersion = ConfuserVersion.v10_r48717;
				else
					continue;

				var proxyType = getProxyCreatorType(method);
				if (proxyType == ProxyCreatorType.None)
					continue;

				setDelegateCreatorMethod(method);
				methodToInfo.add(method, new ProxyCreatorInfo(method, proxyType, theVersion));
				version = theVersion;
			}
		}

		static ProxyCreatorType getProxyCreatorType(MethodDefinition method) {
			foreach (var instr in method.Body.Instructions) {
				var field = instr.Operand as FieldReference;
				if (field == null)
					continue;
				switch (field.FullName) {
				case "System.Reflection.Emit.OpCode System.Reflection.Emit.OpCodes::Call":
				case "System.Reflection.Emit.OpCode System.Reflection.Emit.OpCodes::Callvirt":
					return ProxyCreatorType.CallOrCallvirt;

				case "System.Reflection.Emit.OpCode System.Reflection.Emit.OpCodes::Newobj":
					return ProxyCreatorType.Newobj;
				}
			}
			return ProxyCreatorType.None;
		}

		public new void find() {
			if (delegateCreatorMethods.Count == 0)
				return;
			var cctor = DotNetUtils.getModuleTypeCctor(module);
			if (cctor == null)
				return;

			Log.v("Finding all proxy delegates");

			var delegateInfos = createDelegateInitInfos(cctor);
			fieldToMethods = createFieldToMethodsDictionary(cctor.DeclaringType);
			if (delegateInfos.Count < fieldToMethods.Count)
				throw new ApplicationException("Missing proxy delegates");
			var delegateToFields = new Dictionary<TypeDefinition, List<FieldDefinition>>();
			foreach (var field in fieldToMethods.getKeys()) {
				List<FieldDefinition> list;
				if (!delegateToFields.TryGetValue((TypeDefinition)field.FieldType, out list))
					delegateToFields[(TypeDefinition)field.FieldType] = list = new List<FieldDefinition>();
				list.Add(field);
			}

			foreach (var kv in delegateToFields) {
				var type = kv.Key;
				var fields = kv.Value;

				Log.v("Found proxy delegate: {0} ({1:X8})", Utils.removeNewlines(type), type.MetadataToken.ToInt32());
				RemovedDelegateCreatorCalls++;

				Log.indent();
				foreach (var field in fields) {
					var proxyMethods = fieldToMethods.find(field);
					if (proxyMethods == null)
						continue;
					var info = delegateInfos.find(field);
					if (info == null)
						throw new ApplicationException("Missing proxy info");

					MethodReference calledMethod;
					OpCode callOpcode;
					getCallInfo(info, field, out calledMethod, out callOpcode);

					if (calledMethod == null)
						continue;
					foreach (var proxyMethod in proxyMethods) {
						add(proxyMethod, new DelegateInfo(field, calledMethod, callOpcode));
						Log.v("Field: {0}, Opcode: {1}, Method: {2} ({3:X8})",
									Utils.removeNewlines(field.Name),
									callOpcode,
									Utils.removeNewlines(calledMethod),
									calledMethod.MetadataToken.ToUInt32());
					}
				}
				Log.deIndent();
				delegateTypesDict[type] = true;
			}
		}

		FieldDefinitionAndDeclaringTypeDict<DelegateInitInfo> createDelegateInitInfos(MethodDefinition method) {
			switch (version) {
			case ConfuserVersion.v10_r42915: return createDelegateInitInfos_v10_r42915(method);
			case ConfuserVersion.v10_r48717: return createDelegateInitInfos_v10_r48717(method);
			default: throw new ApplicationException("Invalid version");
			}
		}

		FieldDefinitionAndDeclaringTypeDict<DelegateInitInfo> createDelegateInitInfos_v10_r42915(MethodDefinition method) {
			var infos = new FieldDefinitionAndDeclaringTypeDict<DelegateInitInfo>();
			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count - 2; i++) {
				var ldstr = instrs[i];
				if (ldstr.OpCode.Code != Code.Ldstr)
					continue;
				var info = ldstr.Operand as string;
				if (info == null)
					continue;

				var ldtoken = instrs[i + 1];
				if (ldtoken.OpCode.Code != Code.Ldtoken)
					continue;
				var delegateField = ldtoken.Operand as FieldDefinition;
				if (delegateField == null)
					continue;
				var delegateType = delegateField.FieldType as TypeDefinition;
				if (!DotNetUtils.derivesFromDelegate(delegateType))
					continue;

				var call = instrs[i + 2];
				if (call.OpCode.Code != Code.Call)
					continue;
				var delegateCreatorMethod = call.Operand as MethodDefinition;
				if (delegateCreatorMethod == null || !isDelegateCreatorMethod(delegateCreatorMethod))
					continue;

				infos.add(delegateField, new DelegateInitInfo(info, delegateField, delegateCreatorMethod));
				i += 2;
			}
			return infos;
		}

		FieldDefinitionAndDeclaringTypeDict<DelegateInitInfo> createDelegateInitInfos_v10_r48717(MethodDefinition method) {
			var infos = new FieldDefinitionAndDeclaringTypeDict<DelegateInitInfo>();
			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count - 1; i++) {
				var ldtoken = instrs[i];
				if (ldtoken.OpCode.Code != Code.Ldtoken)
					continue;
				var delegateField = ldtoken.Operand as FieldDefinition;
				if (delegateField == null)
					continue;
				var delegateType = delegateField.FieldType as TypeDefinition;
				if (!DotNetUtils.derivesFromDelegate(delegateType))
					continue;

				var call = instrs[i + 1];
				if (call.OpCode.Code != Code.Call)
					continue;
				var delegateCreatorMethod = call.Operand as MethodDefinition;
				if (delegateCreatorMethod == null || !isDelegateCreatorMethod(delegateCreatorMethod))
					continue;

				infos.add(delegateField, new DelegateInitInfo(delegateField, delegateCreatorMethod));
				i += 1;
			}
			return infos;
		}

		static FieldDefinitionAndDeclaringTypeDict<List<MethodDefinition>> createFieldToMethodsDictionary(TypeDefinition type) {
			var dict = new FieldDefinitionAndDeclaringTypeDict<List<MethodDefinition>>();
			foreach (var method in type.Methods) {
				if (!method.IsStatic || method.Body == null || method.Name == ".cctor")
					continue;
				var delegateField = getDelegateField(method);
				if (delegateField == null)
					continue;
				var methods = dict.find(delegateField);
				if (methods == null)
					dict.add(delegateField, methods = new List<MethodDefinition>());
				methods.Add(method);
			}
			return dict;
		}

		static FieldDefinition getDelegateField(MethodDefinition method) {
			if (method == null || method.Body == null)
				return null;

			FieldDefinition field = null;
			bool foundInvoke = false;
			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code == Code.Ldsfld) {
					var field2 = instr.Operand as FieldDefinition;
					if (field2 == null || field2.DeclaringType != method.DeclaringType)
						continue;
					if (field != null)
						return null;
					if (!DotNetUtils.derivesFromDelegate(field2.FieldType as TypeDefinition))
						continue;
					field = field2;
				}
				else if (instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) {
					var calledMethod = instr.Operand as MethodReference;
					foundInvoke |= calledMethod != null && calledMethod.Name == "Invoke";
				}
			}
			return foundInvoke ? field : null;
		}

		public void cleanUp() {
			if (!Detected)
				return;
			var cctor = DotNetUtils.getModuleTypeCctor(module);
			if (cctor == null)
				return;
			cctor.Body.Instructions.Clear();
			cctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		}
	}
}
