using System.Collections.Generic;

namespace Main {
   public static class TypeInfo {
      public readonly static Dictionary<string /*c type*/, string /*csharp equivalent*/> basicTypes = new Dictionary<string, string>() {
         { "bool", "byte" },
         { "_Bool", "byte" },
         { "unsigned char", "byte" },
         { "signed char", "sbyte" }, // signed specifier can be ignored other than "signed char"
         { "char", "byte" },
         { "size_t", "uint" },
         { "char* []", "byte**" },
         { "unsigned int", "uint" },
         { "unsigned short", "ushort" },
         { "unsigned short int", "ushort" },
         { "unsigned long", "ulong" },
         { "unsigned long int", "ulong" },
         { "unsigned long long int", "ulong" },
         { "short int", "short" },
         { "long int", "long" },
         { "long long int", "long" },
         { "long double", "double" },
      };
   }
}
