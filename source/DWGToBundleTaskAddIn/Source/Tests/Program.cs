using System;
using System.IO;
using System.Text;
using System.Linq;
using DWGToBundleTaskAddIn;

internal class Program
{
    static int checks;
    static void Assert(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Assert(true,name); return; } throw new Exception("ACCEPTED " + name); }
    // Minimal real PDFs with correct xref offsets, including zero-page and mixed-page documents.
    static void Pdf(string path, params string[] contents)
    {
        var objects = new System.Collections.Generic.List<string>();
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Count " + contents.Length + " /Kids [" + string.Join(" ", Enumerable.Range(0,contents.Length).Select(i=>(3+2*i)+" 0 R")) + "] >>");
        for (int i=0;i<contents.Length;i++)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Contents " + (4+2*i) + " 0 R >>");
            objects.Add("<< /Length " + Encoding.ASCII.GetByteCount(contents[i]) + " >>\nstream\n" + contents[i] + "\nendstream");
        }
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new System.Collections.Generic.List<int>();
        for(int i=0;i<objects.Count;i++) { offsets.Add(sb.Length); sb.Append((i+1)+" 0 obj\n"+objects[i]+"\nendobj\n"); }
        int xref=sb.Length;
        sb.Append("xref\n0 " + (objects.Count+1) + "\n0000000000 65535 f \n");
        foreach(int n in offsets) sb.Append(n.ToString("D10")+" 00000 n \n");
        sb.Append("trailer\n<< /Size " + (objects.Count+1) + " /Root 1 0 R >>\nstartxref\n"+xref+"\n%%EOF\n");
        File.WriteAllText(path,sb.ToString(),Encoding.ASCII);
    }
    static void Main(string[] args)
    {
        string gs=Path.GetFullPath(args[0]), root=Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
        string black="0 g 10 10 200 200 re f", white="1 g 0 0 595 842 re f";
        string[] names={"white", "painted-white", "drawing", "mixed", "multi", "zero", "white-overlay", "large-white", "sparse"};
        Pdf(Path.Combine(root,names[0]+".pdf"), "");
        Pdf(Path.Combine(root,names[1]+".pdf"), white);
        Pdf(Path.Combine(root,names[2]+".pdf"), black);
        Pdf(Path.Combine(root,names[3]+".pdf"), black, white);
        Pdf(Path.Combine(root,names[4]+".pdf"), black, black);
        Pdf(Path.Combine(root,names[5]+".pdf"));
        Pdf(Path.Combine(root,names[6]+".pdf"), black+"\n"+white);
        Pdf(Path.Combine(root,names[7]+".pdf"), "%"+new string('x',500000)+"\n"+white);
        Pdf(Path.Combine(root,names[8]+".pdf"), "0 G 0.1 w 10 10 m 500 800 l S");
        foreach(string name in new[]{"white","painted-white","mixed","zero","white-overlay","large-white"})
            Reject(()=>PdfValidator.Validate(Path.Combine(root,name+".pdf"),gs,root,30000),name);
        Assert(PdfValidator.Validate(Path.Combine(root,"drawing.pdf"),gs,root,30000)==1,"drawing passes");
        Assert(PdfValidator.Validate(Path.Combine(root,"multi.pdf"),gs,root,30000)==2,"two pages pass");
        Assert(PdfValidator.Validate(Path.Combine(root,"sparse.pdf"),gs,root,30000)==1,"thin line passes");
        File.WriteAllText(Path.Combine(root,"broken.pdf"),"%PDF-1.4\ntruncated");
        Reject(()=>PdfValidator.Validate(Path.Combine(root,"broken.pdf"),gs,root,30000),"broken PDF");
        File.WriteAllText(Path.Combine(root,"empty.pdf"),"");
        Reject(()=>PdfValidator.Validate(Path.Combine(root,"empty.pdf"),gs,root,30000),"zero bytes");
        Reject(()=>PdfValidator.Validate(Path.Combine(root,"missing.pdf"),gs,root,30000),"missing PDF");
        Reject(()=>PdfValidator.ParseCoverage(""),"no page report");
        Reject(()=>PdfValidator.ParseCoverage("0.00000 0.00000 0.00000 0.00000 CMYK OK\n"),"white coverage");
        Reject(()=>PdfValidator.ParseCoverage("0.0 0.0 0.0 0.5 CMYK ERROR"),"failed page report");
        Reject(()=>PdfValidator.ParseCoverage("0.0 0.0 0.0 1.5 CMYK OK"),"out of range coverage");
        var old=System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture=new System.Globalization.CultureInfo("ru-RU");
        Assert(PdfValidator.ParseCoverage("0.00000 0.00000 0.00000 0.00001 CMYK OK")==1,"locale independent minimal ink");
        System.Globalization.CultureInfo.CurrentCulture=old;
        bool timedout=false;
        try { PdfValidator.Validate(Path.Combine(root,"drawing.pdf"),gs,root,0); } catch(TimeoutException) { timedout=true; }
        Assert(timedout,"expired budget rejects");
        string copy=Path.Combine(root,"existing.pdf"), recovery=Path.Combine(root,"recovery");
        File.WriteAllText(copy,"existing valid result");
        using(new LocalPdfGuard(copy,recovery)) { Assert(!File.Exists(copy),"old PDF hidden from converter"); File.WriteAllText(copy,"bad new result"); }
        Assert(File.ReadAllText(copy)=="existing valid result","existing PDF restored");
        Assert(Directory.GetFiles(recovery).Length==0,"successful restore cleans backup");
        try { using(new LocalPdfGuard(copy,recovery)) { throw new InvalidOperationException(); } } catch(InvalidOperationException) { }
        Assert(File.ReadAllText(copy)=="existing valid result","converter failure preserves PDF");
        File.Delete(copy);
        using(new LocalPdfGuard(copy,recovery)) File.WriteAllText(copy,"invalid new PDF");
        Assert(!File.Exists(copy),"rejected intermediate removed");
        Assert(Directory.GetFiles(root,"ink-*.txt").Length==0,"validation reports cleaned up");
        foreach(string path in args.Skip(2)) Assert(PdfValidator.Validate(path,gs,root,60000)>0,"real drawing " + Path.GetFileName(path));
        checks += CardStatusTests.Run();
        checks += PublicationTests.Run();
        Console.WriteLine("TOTAL " + checks + " passed");
    }
}
