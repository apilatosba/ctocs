using System.Collections.Generic;

namespace Main {
   public static class TypeInfo {
      // one to ones are not included
      public readonly static Dictionary<string /*c type*/, string /*csharp equivalent*/> basicTypes = new Dictionary<string, string>() {
         { "bool", "byte" },
         { "_Bool", "byte" },
         { "unsigned char", "byte" },
         { "signed char", "sbyte" }, // signed specifier can be ignored other than "signed char"
         { "char", "byte" },
         { "unsigned int", "uint" },
         { "unsigned short", "ushort" },
         { "unsigned short int", "ushort" },
         { "unsigned long", "ulong" },
         { "unsigned long int", "ulong" },
         { "unsigned long long", "ulong" },
         { "unsigned long long int", "ulong" },
         { "short int", "short" },
         { "long", "nint" },
         { "long int", "nint" },
         { "long long", "long" },
         { "long long int", "long" },
         { "long unsigned int", "uint" },
         { "long double", "double" },
         { "__builtin_va_list", "RuntimeArgumentHandle" },  // basicTypes OMEGALUL
         { "va_list", "RuntimeArgumentHandle" },            // basicTypes OMEGALUL
      };

      public readonly static HashSet<string> allowedFixedSizeBufferTypes = new HashSet<string>() {
         // Fixed size buffer type must be one of the following: bool, byte, short, int, long, char, sbyte, ushort, uint, ulong, float or double [Main]csharp(CS1663)
         "bool",
         "byte",
         "short",
         "int",
         "long",
         "char",
         "sbyte",
         "ushort",
         "uint",
         "ulong",
         "float",
         "double",
      };

      // used to check if member type of a struct is known. If it is one of these types then it is okay.
      // This set doesnt necessarily need to include all the types but it needs to include all the ones that are generated by ctocs
      public readonly static HashSet<string> builtinCSharpTypes = new HashSet<string>() {
         "bool",
         "byte",
         "sbyte",
         "short",
         "ushort",
         "int",
         "uint",
         "long",
         "ulong",
         "float",
         "double",
         "char",
         "string",
         "object",
         "decimal",
         "void",
         "RuntimeArgumentHandle", // builtin OMEGALUL
      };

      // NOTE: note on the va_list RuntimeArgumentHandle approach. i think this only works on windows. i didnt test it. when i tried it on debian12 it said "System.PlatformNotSupportedException: ArgIterator is not supported on this platform."
      //       surely is supported on windows Clueless. whatever there is nothing i can do about it. certified microsoft moment

      public static readonly HashSet<string> csharpReservedKeywords = new HashSet<string>() {
         "__arglist", "__makeref", "__reftype", "__refvalue",
         "abstract", "as",
         "base", "bool", "break", "byte",
         "case", "catch", "char", "checked", "class", "const", "continue",
         "decimal", "default", "delegate", "do", "double",
         "else", "enum", "event", "explicit", "extern",
         "false", "finally", "fixed", "float", "for", "foreach",
         "goto",
         "if", "implicit", "in", "int", "interface", "internal", "is",
         "lock", "long",
         "namespace", "new", "nint", "nuint", "null",
         "object", "operator", "out", "override",
         "params", "private", "protected", "public",
         "readonly", "ref", "return",
         "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
         "this", "throw", "true", "try", "typeof",
         "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
         "virtual", "void", "volatile",
         "while"
      };

      public static readonly HashSet<string> csharpReservedKeywordsThatAreTypes = new HashSet<string>() {
         "bool", "byte",
         "char",
         "decimal", "double",
         "float",
         "int",
         "long",
         "nint", "nuint",
         "object",
         "sbyte", "short", "string",
         "uint", "ulong", "ushort"
      };

      public static readonly HashSet<string> csharpReservedKeywordsThatAreNotTypes;

      static TypeInfo() {
         csharpReservedKeywordsThatAreNotTypes = new HashSet<string>(csharpReservedKeywords);
         csharpReservedKeywordsThatAreNotTypes.ExceptWith(csharpReservedKeywordsThatAreTypes);
      }

      /*
         auto	else	long	switch
         break	enum	register	typedef
         case	extern	return	union
         char	float	short	unsigned
         const	for	signed	void
         continue	goto	sizeof	volatile
         default	if	static	while
         do	int	struct	_Packed
         double
      */
      public static readonly HashSet<string> cReservedKeywordsThatAreTypes = new HashSet<string>() {
         "char",
         "double",
         "float",
         "int",
         "long",
         "short",
      };
   }
}
