using System;
using DWGToBundleTaskAddIn;
using EPDM.Interop.epdm;

internal static class CardStatusTests
{
    static int count;
    static void Check(bool ok, string name) { if(!ok) throw new Exception(name); count++; Console.WriteLine("PASS card " + name); }
    static void Fails(FakeVault vault, string name)
    {
        try { SourcePdfStatus.MarkRejected(vault,42,7); }
        catch(InvalidOperationException) { Check(true,name); return; }
        throw new Exception("Unexpected card success: " + name);
    }
    public static int Run()
    {
        var v=new FakeVault(); SourcePdfStatus.MarkRejected(v,42,7);
        Check(v.WrittenFile==42 && v.WrittenVar==9 && (int)v.WrittenValue==0,"writes 0 on original ID");
        Check(v.WrittenFlags==(int)EdmBatchFlags.EdmBatch_AllConfigs,"all configurations");
        Check(v.Verified==2 && v.Closed,"read back and close");
        v=new FakeVault{MissingFile=true}; Fails(v,"missing original"); Check(!v.Committed,"missing original no write");
        v=new FakeVault{MissingVar=true}; Fails(v,"missing pdf variable");
        v=new FakeVault{Flags=0}; Fails(v,"versioned variable rejected"); Check(!v.Committed,"no implicit checkout");
        v=new FakeVault{Locked=true}; Fails(v,"checked out original rejected");
        v=new FakeVault{CommitErrors=1}; Fails(v,"PDM commit error");
        v=new FakeVault{ReturnedValue=1}; Fails(v,"readback differs"); Check(v.Closed,"reader closed on failure");
        v=new FakeVault{ReadFound=false}; Fails(v,"missing card value");
        v=new FakeVault{ThrowOnCommit=true}; Fails(v,"permission error");
        return count;
    }
}
// Minimal test doubles for control flow only; production compilation uses the real EPDM interop DLL.
namespace EPDM.Interop.epdm
{
    enum EdmObjectType { EdmObject_File } enum EdmUtility { EdmUtil_BatchUpdate }
    [Flags] enum EdmVariableFlags { EdmVar_VerFreeUpdateAll=4, EdmVar_VerFreeUpdateLatest=8 }
    [Flags] enum EdmBatchFlags { EdmBatch_AllConfigs=1 }
    class EdmBatchError2 { }
    interface IEdmVault11 { object GetObject(EdmObjectType type,int id); object CreateUtility(EdmUtility type); }
    interface IEdmVariableMgr5 { Variable GetVariable(string name); }
    class Variable { public int ID=9; public int Flags=8; }
    interface IEdmFile5 { bool IsLocked {get;} IEdmStrLst5 GetConfigurations(ref object version); object GetEnumeratorVariable(); }
    interface IEdmStrLst5 { Position GetHeadPosition(); string GetNext(Position pos); }
    class Position { public int Index; public bool IsNull { get {return Index>=2;} } }
    interface IEdmBatchUpdate2 { void SetVar(int file,int variable,ref object value,string config,int flags); int CommitUpdate(out EdmBatchError2[] errors,object cb); }
    interface IEdmEnumeratorVariable10 { bool GetVar2(string variable,string config,int folder,out object value); void CloseFile(bool flush); }
    class FakeVault : IEdmVault11, IEdmVariableMgr5, IEdmFile5, IEdmBatchUpdate2, IEdmEnumeratorVariable10, IEdmStrLst5
    {
        public bool MissingFile,MissingVar,Locked,Committed,Closed,ThrowOnCommit; public bool ReadFound=true;
        public int Flags=8,CommitErrors,WrittenFile,WrittenVar,WrittenFlags,Verified; public object WrittenValue,ReturnedValue=0;
        public object GetObject(EdmObjectType type,int id) {if(id!=42) throw new Exception("wrong source"); return MissingFile?null:this;}
        public object CreateUtility(EdmUtility type) {return this;}
        public Variable GetVariable(string name) {if(name!="pdf") throw new Exception("wrong variable"); return MissingVar?null:new Variable{Flags=Flags};}
        public bool IsLocked {get{return Locked;}}
        public IEdmStrLst5 GetConfigurations(ref object version) {return this;}
        public object GetEnumeratorVariable() {return this;}
        public Position GetHeadPosition() {return new Position();}
        public string GetNext(Position pos) {return pos.Index++==0?"Model":"Лист_1";}
        public void SetVar(int file,int variable,ref object value,string config,int flags) {WrittenFile=file;WrittenVar=variable;WrittenValue=value;WrittenFlags=flags;}
        public int CommitUpdate(out EdmBatchError2[] errors,object cb) {if(ThrowOnCommit) throw new InvalidOperationException("Denied");Committed=true;errors=new EdmBatchError2[CommitErrors];return CommitErrors;}
        public bool GetVar2(string variable,string config,int folder,out object value) {if(folder!=7) throw new Exception("wrong folder"); Verified++;value=ReturnedValue;return ReadFound;}
        public void CloseFile(bool flush) {Closed=true;}
    }
}
