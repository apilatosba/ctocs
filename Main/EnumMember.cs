namespace Main {
   public class EnumMember {
      public string identifier;
      public string value; // optional

      public EnumMember Copy() {
         return (EnumMember)MemberwiseClone();
      }
   }
}