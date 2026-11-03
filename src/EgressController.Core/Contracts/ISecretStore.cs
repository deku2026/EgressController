namespace EgressController.Core.Contracts;
public interface ISecretStore
{
    string? Read(string account);
    void Write(string account, string secret);
}
