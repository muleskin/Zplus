using UIKit;

namespace ZPlus.Mobile;

public class Program
{
    // This is the main entry point of the application.
    static void Main(string[] args)
    {
        // If you want to use a different Application Delegate class from "AppDelegate"
        // you can specify it here.
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
