using System.Collections.Generic;

namespace Main {
   public class FunctionPointerData {
      public string returnType;
      public int amountOfStars; // usually will be 1
      public string name;
      public string arrayPart;
      public List<IFunctionParameterData> parameters;
      public bool isVariadic;
   }
}