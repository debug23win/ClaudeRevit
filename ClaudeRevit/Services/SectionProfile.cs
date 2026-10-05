namespace ClaudeRevit.Services;

public static class SectionProfile
{
    public static double Area(string shape,double width,double height,double web,double flange)
    {
        if(new[]{width,height,web,flange}.Any(x=>!double.IsFinite(x)||x<=0) || shape is not ("rectangle" or "box" or "i" or "channel" or "angle" or "tube" or "timber_pair"))throw new ArgumentException("Unknown section shape or invalid dimensions.");
        if((shape is "box" or "i" or "channel") && (2*web>=width||2*flange>=height))throw new ArgumentException("Section thickness closes the internal opening.");
        if(shape=="angle"&&(web>=width||flange>=height))throw new ArgumentException("Angle thickness closes the section.");
        if(shape=="timber_pair"&&web>=width)throw new ArgumentException("Timber gap must be less than overall width.");
        if(shape=="tube"&&(Math.Abs(height-width)>1e-6||2*web>=width))throw new ArgumentException("Tube needs equal overall width/height (diameter) and a positive internal bore.");
        double area=shape switch {"rectangle"=>width*height,"box"=>width*height-(width-2*web)*(height-2*flange),"i"=>2*width*flange+(height-2*flange)*web,"channel"=>height*web+2*(width-web)*flange,"angle"=>height*web+(width-web)*flange,"timber_pair"=>(width-web)*height,_=>Math.PI*(width*width-Math.Pow(width-2*web,2))/4};
        if(!double.IsFinite(area)||area<=0)throw new ArgumentException("Section area overflows or is empty.");return area;
    }
    public static List<double[][]> Loops(string shape,double w,double h,double t,double f)
    {
        Area(shape,w,h,t,f);
        if(shape=="tube")throw new ArgumentException("Circular tubes require native arcs, not a polygon approximation.");
        if(shape=="channel")return [[ [0,0],[w,0],[w,f],[t,f],[t,h-f],[w,h-f],[w,h],[0,h] ]];
        if(shape=="angle")return [[ [0,0],[w,0],[w,f],[t,f],[t,h],[0,h] ]];
        if(shape=="timber_pair")return [[ [0,0],[(w-t)/2,0],[(w-t)/2,h],[0,h] ],[ [(w+t)/2,0],[w,0],[w,h],[(w+t)/2,h] ]];
        if(shape=="i")
        {var l=(w-t)/2;var r=(w+t)/2;return [[ [0,0],[w,0],[w,f],[r,f],[r,h-f],[w,h-f],[w,h],[0,h],[0,h-f],[l,h-f],[l,f],[0,f] ]];}
        List<double[][]> loops=[[ [0,0],[w,0],[w,h],[0,h] ]];
        if(shape=="box")loops.Add([ [t,f],[t,h-f],[w-t,h-f],[w-t,f] ]);
        return loops;
    }
}
