using System;
using System.IO;
using System.Threading.Tasks;
using DWGToBundleTaskAddIn;

internal static class PublicationTests
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS publication " + label); }
    static void Fails(Action action, string label) { try {action();} catch(Exception) {Check(true,label);return;} throw new Exception("Accepted " + label); }
    public static int Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"dwg-publish-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string input=Path.Combine(root,"new.pdf"), output=Path.Combine(root,"out.pdf");
            File.WriteAllText(input,"VALIDATED NEW CONTENT");
            var vault=new FakeArchive(root);
            PdfPublication.Save(vault,output,input,root);
            Check(vault.Stored=="VALIDATED NEW CONTENT" && vault.File.Version==1,"new file committed and verified");
            File.WriteAllText(input,"NEXT VERSION");
            PdfPublication.Save(vault,output,input,root);
            Check(vault.Stored=="NEXT VERSION" && vault.File.Version==2 && !vault.File.Locked,"existing file new version");
            vault.FailRegisterEditedFile=true;
            File.WriteAllText(input,"BROKEN UPDATE");
            Fails(()=>PdfPublication.Save(vault,output,input,root),"failed checkin reported");
            Check(vault.Stored=="NEXT VERSION" && vault.File.Version==2 && !vault.File.Locked && vault.Undos==1,"old version preserved and checkout undone");
            vault.FailRegisterEditedFile=false;
            vault.DenyUnregistration=true;
            Fails(()=>PdfPublication.Save(vault,output,input,root),"checkout denied");
            Check(vault.Stored=="NEXT VERSION" && vault.File.Version==2,"checkout failure preserves old version");
            vault.DenyUnregistration=false;
            vault.File.Locked=true;
            Fails(()=>PdfPublication.Save(vault,output,input,root),"busy PDF rejected");
            vault.File.Locked=false;
            vault.CorruptReadback=true;
            File.WriteAllText(input,"THIRD VERSION");
            Fails(()=>PdfPublication.Save(vault,output,input,root),"wrong archived bytes rejected");
            Check(vault.File.Version==3 && vault.Stored=="THIRD VERSION","checkin not rolled back after commit");
            vault.CorruptReadback=false;
            vault.SuppressNewVersion=true;
            Fails(()=>PdfPublication.Save(vault,output,input,root),"checkin without new version rejected");
            Check(vault.File.Version==3 && !vault.File.Locked,"unchanged archive version is not reported as published");
            vault.SuppressNewVersion=false;
            string outside=Path.Combine(root,"outside.pdf");File.WriteAllText(outside,"PRIOR RESULT");
            PdfPublication.SaveOutsideVault(outside,input);
            Check(File.ReadAllText(outside)=="THIRD VERSION","outside vault replaced and verified");
            string busy=Path.Combine(root,"drawing.pdf");
            using(ConversionLease.Acquire(busy,output))
            {
                Fails(()=>{using(ConversionLease.Acquire(busy,output)){}},"same-process repeat blocked");
                var t=Task.Run(()=> {try {using(ConversionLease.Acquire(busy,output)){} return false;}catch(ConversionBusyException){return true;}});
                Check(t.Result,"other thread blocked");
                using(ConversionLease.Acquire(Path.Combine(root,"other.pdf"))) Check(true,"unrelated input allowed");
            }
            using(ConversionLease.Acquire(busy,output)) Check(true,"lease released for retry");
            return checks;
        }
        finally {Directory.Delete(root,true);}
    }
    sealed class FakeArchive : IPdfArchive
    {
        public ArchivePdf File; public string Stored;public bool FailRegisterEditedFile,DenyUnregistration,CorruptReadback,SuppressNewVersion;public int Undos;
        readonly string local;
        public FakeArchive(string root) {local=Path.Combine(root,"cache.pdf");}
        public ArchivePdf Find(string output) {return File==null?null:new ArchivePdf{Id=File.Id,FolderId=File.FolderId,Version=File.Version,Locked=File.Locked,LocalPath=local};}
        public ArchivePdf Add(string output,string validated) {Stored=System.IO.File.ReadAllText(validated);System.IO.File.WriteAllText(local,Stored);File=new ArchivePdf{Id=10,FolderId=2,Version=1,Locked=false,LocalPath=local};return Find(output);}
        public void UnregisterForEditing(ArchivePdf file) {if(DenyUnregistration)throw new IOException("Denied");File.Locked=true;System.IO.File.WriteAllText(local,Stored);}
        public void RegisterEditedFile(ArchivePdf file) {if(FailRegisterEditedFile)throw new IOException("Commit failed");if(!SuppressNewVersion)File.Version++;File.Locked=false;Stored=System.IO.File.ReadAllText(local);}
        public void CancelUnregistration(ArchivePdf file) {Undos++;File.Locked=false;System.IO.File.WriteAllText(local,Stored);}
        public void ReadVersion(ArchivePdf file,string destination) {System.IO.File.WriteAllText(destination,CorruptReadback?"BAD ARCHIVE":Stored);}
    }
}
