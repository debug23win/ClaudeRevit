namespace ClaudeRevit.Services;

public static class SectionProfile
{
    public static double Area(string shape,double width,double height,double web,double flange)
    {
        if(new[]{width,height,web,flange}.Any(x=>!double.IsFinite(x)||x<=0) || shape is not ("rectangle" or "box" or "i"))throw new ArgumentException("Unknown section shape or invalid dimensions.");
        if(shape!="rectangle"&&(2*web>=width||2*flange>=height))throw new ArgumentException("Section thickness closes the internal opening.");
        return shape switch {"rectangle"=>width*height,"box"=>width*height-(width-2*web)*(height-2*flange),_=>2*width*flange+(height-2*flange)*web};
    }
    public static List<double[][]> Loops(string shape,double w,double h,double t,double f)
    {
        Area(shape,w,h,t,f);
        if(shape=="i")
        {var l=(w-t)/2;var r=(w+t)/2;return [[ [0,0],[w,0],[w,f],[r,f],[r,h-f],[w,h-f],[w,h],[0,h],[0,h-f],[l,h-f],[l,f],[0,f] ]];}
        List<double[][]> loops=[[ [0,0],[w,0],[w,h],[0,h] ]];
        if(shape=="box")loops.Add([ [t,f],[t,h-f],[w-t,h-f],[w-t,f] ]);
        return loops;
    }
}
