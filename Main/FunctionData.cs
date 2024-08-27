using System.Collections.Generic;

namespace Main {
   public class FunctionData {
      public string returnType;
      public string name;
      public List<IFunctionParameterData> parameters;
      public bool isVariadic;
   }
}