//
// Class.cs
//
// Copyright 2009 Novell, Inc
// Copyright 2011 - 2015 Xamarin Inc. All rights reserved.
//

// #define LOG_TYPELOAD

using System;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Foundation;
#if !COREBUILD
using Registrar;
#endif

namespace ObjCRuntime {
	public partial class Class : INativeObject
	{
#if !COREBUILD
		public static bool ThrowOnInitFailure = true;

		static Dictionary<Type, IntPtr> type_to_class; // accessed from multiple threads, locking required.

		internal IntPtr handle;

		[BindingImpl (BindingImplOptions.Optimizable)]
		internal unsafe static void Initialize (Runtime.InitializationOptions* options)
		{
			type_to_class = new Dictionary<Type, IntPtr> (Runtime.TypeEqualityComparer);

			if (!Runtime.DynamicRegistrationSupported)
				return; // Only the dynamic registrar needs the list of registered assemblies.

			var map = options->RegistrationMap;
			if (map == null)
				return;
			
			for (int i = 0; i < map->assembly_count; i++) {
				var ptr = Marshal.ReadIntPtr (map->assembly, i * IntPtr.Size);
				Runtime.Registrar.SetAssemblyRegistered (Marshal.PtrToStringAuto (ptr));
			}
		}

		public Class (string name)
		{
			this.handle = objc_getClass (name);

			if (this.handle == IntPtr.Zero)
				throw new ArgumentException (String.Format ("'{0}' is an unknown class", name));
		}

		public Class (Type type)
		{
			this.handle = GetClassHandle (type);
		}

		public Class (IntPtr handle)
		{
			this.handle = handle;
		}

		internal static Class Construct (IntPtr handle) 
		{
			return new Class (handle);
		}

		public IntPtr Handle {
			get { return this.handle; }
		}

		public IntPtr SuperClass {
			get { return class_getSuperclass (handle); }
		}

		public unsafe string Name {
			get {
				IntPtr ptr = class_getName (this.handle);
				return Marshal.PtrToStringAuto (ptr);
			}
		}

		public static IntPtr GetHandle (string name)
		{
			return objc_getClass (name);
		}

		// This method is treated as an intrinsic operation by
		// the aot compiler, generating a static reference to the
		// class (it will be faster than GetHandle, but it will
		// not compile unless the class in question actually exists
		// as an ObjectiveC class in the binary).
		public static IntPtr GetHandleIntrinsic (string name) {
			return objc_getClass (name);
		}

		public static IntPtr GetHandle (Type type) {
			return GetClassHandle (type);
		}

		[BindingImpl (BindingImplOptions.Optimizable)] // To inline the Runtime.DynamicRegistrationSupported code if possible.
		static IntPtr GetClassHandle (Type type)
		{
			IntPtr @class = IntPtr.Zero;

			// We cache results in a dictionary (type_to_class) - we put failures (when @class = IntPtr.Zero) in the dictionary as well.
			// We do as little as possible with the lock held (only fetch/add to the dictionary, nothing else)

			bool found;
			lock (type_to_class)
				found = type_to_class.TryGetValue (type, out @class);

			if (!found) {
				@class = FindClass (type, out var is_custom_type);
				lock (type_to_class)
					type_to_class [type] = @class;
			}

			if (@class == IntPtr.Zero) {
				if (!Runtime.DynamicRegistrationSupported)
					throw ErrorHelper.CreateError (8026, $"Can't register the class {type.FullName} when the dynamic registrar has been linked away.");
				@class = Register (type);
				lock (type_to_class)
					type_to_class [type] = @class;
			}

			return @class;
		}

		internal static IntPtr GetClassForObject (IntPtr obj)
		{
			return Messaging.IntPtr_objc_msgSend (obj, Selector.GetHandle (Selector.Class));
		}

		// note: PreserveCode.cs keep this around only for debug builds (see: monotouch-glue.m)
		internal static string LookupFullName (IntPtr klass)
		{
			Type type = Lookup (klass);
			return type == null ? null : type.FullName;
		}

		public static Type Lookup (Class @class)
		{
			return Lookup (@class.Handle, true);
		}

		internal static Type Lookup (IntPtr klass)
		{
			return LookupClass (klass, true);
		}

		internal static Type Lookup (IntPtr klass, bool throw_on_error)
		{
			return LookupClass (klass, throw_on_error);
		}

		[BindingImpl (BindingImplOptions.Optimizable)] // To inline the Runtime.DynamicRegistrationSupported code if possible.
		static Type LookupClass (IntPtr klass, bool throw_on_error)
		{
			bool is_custom_type;
			var find_class = klass;
			do {
				var tp = FindType (find_class, out is_custom_type);
				if (tp != null)
					return tp;
				if (Runtime.DynamicRegistrationSupported)
					break; // We can't continue looking up the hierarchy if we have the dynamic registrar, because we might be supposed to register this class.
				find_class = class_getSuperclass (find_class);
			} while (find_class != IntPtr.Zero);

			// The linker will remove this condition (and the subsequent method call) if possible
			if (Runtime.DynamicRegistrationSupported)
				return Runtime.Registrar.Lookup (klass, throw_on_error);

			if (throw_on_error)
				throw ErrorHelper.CreateError (8026, $"Can't lookup the Objective-C class 0x{klass.ToString ("x")} ({class_getName (klass)}) when the dynamic registrar has been linked away.");

			return null;
		}

		internal static IntPtr Register (Type type)
		{
			return Runtime.Registrar.Register (type);
		}

		// Find the given managed type in the tables generated by the static registrar.
		unsafe static IntPtr FindClass (Type type, out bool is_custom_type)
		{
			var map = Runtime.options->RegistrationMap;

			is_custom_type = false;

			if (map == null) {
				// Using only the dynamic registrar
				return IntPtr.Zero;
			}

			if (type.IsGenericType)
				type = type.GetGenericTypeDefinition ();

			// Look for the type in the type map.
			var asm_name = type.Assembly.GetName ().Name;
			var mod_token = type.Module.MetadataToken;
			var type_token = type.MetadataToken & ~0x02000000;
			for (int i = 0; i < map->map_count; i++) {
				var token_reference = map->map [i].type_reference;
				if (!CompareTokenReference (asm_name, mod_token, type_token, token_reference))
					continue;

				var rv = map->map [i].handle;
				is_custom_type = i >= (map->map_count - map->custom_type_count);
#if LOG_TYPELOAD
				Console.WriteLine ($"FindClass ({type.FullName}, {is_custom_type}): 0x{rv.ToString ("x")} = {class_getName (rv)}.");
#endif
				return rv;
			}

			// The type we're looking for might be a type the registrar skipped, in which case we must
			// find it in the table of skipped types
			for (int i = 0; i < map->skipped_map_count; i++) {
				var token_reference = map->skipped_map [i].skipped_reference;
				if (!CompareTokenReference (asm_name, mod_token, type_token, token_reference))
					continue;

				// This is a skipped type, we now got the index into the normal table.
				var idx = map->skipped_map [i].index;
				is_custom_type = idx >= (map->map_count - map->custom_type_count);
				return map->map [idx].handle;
			}

			return IntPtr.Zero;
		}

		unsafe static bool CompareTokenReference (string asm_name, int mod_token, int type_token, uint token_reference)
		{
			var map = Runtime.options->RegistrationMap;
			IntPtr assembly_name;

			if ((token_reference & 0x1) == 0x1) {
				// full token reference
				var idx = (int) (token_reference >> 1);
				var entry = Runtime.options->RegistrationMap->full_token_references + (IntPtr.Size + 8) * idx;
				// first compare what's most likely to fail (the type's metadata token)
				var token = (uint) Marshal.ReadInt32 (entry + IntPtr.Size + 4);
				if (type_token != token)
					return false;

				// then the module token
				var module_token = (uint) Marshal.ReadInt32 (entry + IntPtr.Size);
				if (mod_token != module_token)
					return false;

				// leave the assembly name for the end, since it's the most expensive comparison (string comparison)
				assembly_name = Marshal.ReadIntPtr (entry);
			} else {
				// packed token reference
				if (token_reference >> 8 != type_token)
					return false;

				var assembly_index = (token_reference >> 1) & 0x7F;
				assembly_name = Marshal.ReadIntPtr (map->assembly, (int) assembly_index * IntPtr.Size);
			}

			return Runtime.StringEquals (assembly_name, asm_name);
		}

		internal unsafe static Type FindType (IntPtr @class, out bool is_custom_type)
		{
			var map = Runtime.options->RegistrationMap;
			Runtime.MTClassMap? entry = null;

			is_custom_type = false;

			if (map == null) {
#if LOG_TYPELOAD
				Console.WriteLine ($"FindType (0x{@class:X} = {Marshal.PtrToStringAuto (class_getName (@class))}) => found no map.");
#endif
				return null;
			}

			// Find the ObjC class pointer in our map
			// Potential improvement: order the type handles after loading them, which means we could do a binary search here.
			// A binary search will likely be faster than a dictionary for any real-world scenario (and if slower, not much slower),
			// but it would need a lot less memory (very little when sorting, could probably use stack memory, and then nothing at all afterwards).
			for (int i = 0; i < map->map_count; i++) {
				if (map->map [i].handle != @class)
					continue;

				entry = map->map [i];
				is_custom_type = i >= (map->map_count - map->custom_type_count);
				break;
			}

			if (!entry.HasValue) {
#if LOG_TYPELOAD
				Console.WriteLine ($"FindType (0x{@class:X} = {Marshal.PtrToStringAuto (class_getName (@class))}) => found no type.");
#endif
				return null;
			}

			// Resolve the map entry we found to a managed type
			var member = ResolveTokenReference (entry.Value.type_reference, 0x02000000);
			var type = member as Type;

			if (type == null && member != null)
				throw ErrorHelper.CreateError (8022, $"Expected the token reference 0x{entry.Value.type_reference:X} to be a type, but it's a {member.GetType ().Name}. Please file a bug report at http://bugzilla.xamarin.com.");

#if LOG_TYPELOAD
			Console.WriteLine ($"FindType (0x{@class:X} = {Marshal.PtrToStringAuto (class_getName (@class))}) => {type.FullName}; is custom: {is_custom_type} (token reference: 0x{entry.Value.type_reference:X}).");
#endif

			return type;
		}

		internal unsafe static MemberInfo ResolveFullTokenReference (uint token_reference)
		{
			// sizeof (MTFullTokenReference) = IntPtr.Size + 4 + 4
			var entry = Runtime.options->RegistrationMap->full_token_references + (IntPtr.Size + 8) * (int) (token_reference >> 1);
			var assembly_name = Marshal.ReadIntPtr (entry);
			var module_token = (uint) Marshal.ReadInt32 (entry + IntPtr.Size);
			var token = (uint) Marshal.ReadInt32 (entry + IntPtr.Size + 4);

#if LOG_TYPELOAD
			Console.WriteLine ($"ResolveFullTokenReference (0x{token_reference:X}) assembly name: {assembly_name} module token: 0x{module_token:X} token: 0x{token:X}.");
#endif

			var assembly = ResolveAssembly (assembly_name);
			var module = ResolveModule (assembly, module_token);
			return ResolveToken (module, token);
		}

		internal unsafe static MemberInfo ResolveTokenReference (uint token_reference, uint implicit_token_type)
		{
			var map = Runtime.options->RegistrationMap;

			if ((token_reference & 0x1) == 0x1)
				return ResolveFullTokenReference (token_reference);

			var assembly_index = (token_reference >> 1) & 0x7F;
			uint token = (token_reference >> 8) + implicit_token_type;

#if LOG_TYPELOAD
			Console.WriteLine ($"ResolveTokenReference (0x{token_reference:X}) assembly index: {assembly_index} token: 0x{token:X}.");
#endif

			var assembly_name = Marshal.ReadIntPtr (map->assembly, (int) assembly_index * IntPtr.Size);
			var assembly = ResolveAssembly (assembly_name);
			var module = ResolveModule (assembly, 0x1);

			return ResolveToken (module, token | implicit_token_type);
		}

		static MemberInfo ResolveToken (Module module, uint token)
		{
			// Finally resolve the token.
			var token_type = token & 0xFF000000;
			switch (token & 0xFF000000) {
			case 0x02000000: // TypeDef
				var type = module.ResolveType ((int) token);
#if LOG_TYPELOAD
				Console.WriteLine ($"ResolveToken (0x{token:X}) => Type: {type.FullName}");
#endif
				return type;
			case 0x06000000: // Method
				var method = module.ResolveMethod ((int) token);
#if LOG_TYPELOAD
				Console.WriteLine ($"ResolveToken (0x{token:X}) => Method: {method.DeclaringType.FullName}.{method.Name}");
#endif
				return method;
			default:
				throw ErrorHelper.CreateError (8021, $"Unknown implicit token type: 0x{token_type}.");
			}
		}

		static Module ResolveModule (Assembly assembly, uint token)
		{
			foreach (var mod in assembly.GetModules ()) {
				if (mod.MetadataToken != token)
					continue;

#if LOG_TYPELOAD
				Console.WriteLine ($"ResolveModule (\"{assembly.FullName}\", 0x{token:X}): {mod.Name}.");
#endif
				return mod;
			}

			throw ErrorHelper.CreateError (8020, $"Could not find the module with MetadataToken 0x{token:X} in the assembly {assembly}.");
		}

		static Assembly ResolveAssembly (IntPtr assembly_name)
		{
			// Find the assembly. We've already loaded all the assemblies that contain registered types, so just look at those assemblies.
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies ()) {
				if (!Runtime.StringEquals (assembly_name, asm.GetName ().Name))
					continue;

#if LOG_TYPELOAD
				Console.WriteLine ($"ResolveAssembly (\"{assembly_name}\"): {asm.FullName}.");
#endif
				return asm;
			}

			throw ErrorHelper.CreateError (8019, $"Could not find the assembly {Marshal.PtrToStringAuto (assembly_name)} in the loaded assemblies.");
		}

		internal unsafe static uint GetTokenReference (Type type, bool throw_exception = true)
		{
			if (type.IsGenericType)
				type = type.GetGenericTypeDefinition ();

			var asm_name = type.Module.Assembly.GetName ().Name;

			// First check if there's a full token reference to this type
			var token = GetFullTokenReference (asm_name, type.Module.MetadataToken, type.MetadataToken);
			if (token != uint.MaxValue)
				return token;

			// If type.Module.MetadataToken != 1, then the token must be a full token, which is not the case because we've already checked, so throw an exception.
			if (type.Module.MetadataToken != 1) {
				if (!throw_exception)
					return Runtime.INVALID_TOKEN_REF;
				throw ErrorHelper.CreateError (8025, $"Failed to compute the token reference for the type '{type.AssemblyQualifiedName}' because its module's metadata token is {type.Module.MetadataToken} when expected 1.");
			}
			
			var map = Runtime.options->RegistrationMap;

			// Find the assembly index in our list of registered assemblies.
			int assembly_index = -1;
			for (int i = 0; i < map->assembly_count; i++) {
				var name_ptr = Marshal.ReadIntPtr (map->assembly, (int) i * IntPtr.Size);
				if (Runtime.StringEquals (name_ptr, asm_name)) {
					assembly_index = i;
					break;
				}
			}
			// If the assembly isn't registered, then the token must be a full token (which it isn't, because we've already checked).
			if (assembly_index == -1) {
				if (!throw_exception)
					return Runtime.INVALID_TOKEN_REF;
				throw ErrorHelper.CreateError (8025, $"Failed to compute the token reference for the type '{type.AssemblyQualifiedName}' because the assembly couldn't be found in the list of registered assemblies.");
			}

			if (assembly_index > 127) {
				if (!throw_exception)
					return Runtime.INVALID_TOKEN_REF;
				throw ErrorHelper.CreateError (8025, $"Failed to compute the token reference for the type '{type.AssemblyQualifiedName}' because the assembly index {assembly_index} is not valid (must be <= 127).");
			}

			return (uint) ((type.MetadataToken << 8) + (assembly_index << 1));
			
		}

		// Look for the specified metadata token in the table of full token references.
		static unsafe uint GetFullTokenReference (string assembly_name, int module_token, int metadata_token)
		{
			var map = Runtime.options->RegistrationMap;
			for (int i = 0; i < map->full_token_reference_count; i++) {
				var ptr = map->full_token_references + (i * IntPtr.Size + 8);
				var asm_ptr = Marshal.ReadIntPtr (ptr);
				var token = Marshal.ReadInt32 (ptr + IntPtr.Size + 4);
				if (token != metadata_token)
					continue;
				var mod_token = Marshal.ReadInt32 (ptr + IntPtr.Size);
				if (mod_token != module_token)
					continue;
				if (!Runtime.StringEquals (asm_ptr, assembly_name))
					continue;

				return (uint) i;
			}

			return uint.MaxValue;
		}

		/*
		Type must have been previously registered.
		*/
		[BindingImpl (BindingImplOptions.Optimizable)] // To inline the Runtime.DynamicRegistrationSupported code if possible.
#if !XAMCORE_2_0 && !MONOTOUCH // Accidently exposed this to public, can't break API
		public
#else
		internal
#endif
		static bool IsCustomType (Type type)
		{
			bool is_custom_type;
			var @class = FindClass (type, out is_custom_type);
			if (@class != IntPtr.Zero)
				return is_custom_type;
			
			if (Runtime.DynamicRegistrationSupported)
				return Runtime.Registrar.IsCustomType (type);

			throw ErrorHelper.CreateError (8026, $"Can't determine if {type.FullName} is a custom type when the dynamic registrar has been linked away.");
		}

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal static extern IntPtr objc_allocateClassPair (IntPtr superclass, string name, IntPtr extraBytes);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal static extern IntPtr objc_getClass (string name);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal static extern void objc_registerClassPair (IntPtr cls);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal static extern bool class_addIvar (IntPtr cls, string name, IntPtr size, byte alignment, string types);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal static extern bool class_addMethod (IntPtr cls, IntPtr name, IntPtr imp, string types);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal extern static bool class_addMethod (IntPtr cls, IntPtr name, Delegate imp, string types);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal extern static bool class_addProtocol (IntPtr cls, IntPtr protocol);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal static extern IntPtr class_getName (IntPtr cls);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal static extern IntPtr class_getSuperclass (IntPtr cls);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal static extern IntPtr object_getClass (IntPtr obj);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal extern static IntPtr class_getMethodImplementation (IntPtr cls, IntPtr sel);

		[DllImport ("/usr/lib/libobjc.dylib")]
		internal extern static IntPtr class_getInstanceVariable (IntPtr cls, string name);

		[DllImport ("/usr/lib/libobjc.dylib", CharSet=CharSet.Ansi)]
		internal extern static bool class_addProperty (IntPtr cls, string name, objc_attribute_prop [] attributes, int count);

		[StructLayout (LayoutKind.Sequential, CharSet=CharSet.Ansi)]
		internal struct objc_attribute_prop {
			[MarshalAs (UnmanagedType.LPStr)] internal string name;
			[MarshalAs (UnmanagedType.LPStr)] internal string value;
		}
#endif // !COREBUILD
	}
}
