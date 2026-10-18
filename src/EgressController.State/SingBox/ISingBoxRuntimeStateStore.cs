namespace EgressController.State.SingBox;

/// <summary>Persistence boundary for runtime application and last-good recovery.</summary>
public interface ISingBoxRuntimeStateStore
{
    SingBoxRuntimePointer? LoadCurrentRuntime();
    SingBoxRuntimePointer? LoadLastGoodRuntime();
    SingBoxPendingApply? LoadPendingApply();
    void SaveCurrentRuntime(SingBoxRuntimePointer pointer);
    void SaveLastGoodRuntime(SingBoxRuntimePointer pointer);
    void SavePendingApply(SingBoxPendingApply pending);
    void SaveCurrent(SingBoxCorePointer pointer);
    void SaveLastGood(SingBoxCorePointer pointer);
    void ClearPendingApply();
}
