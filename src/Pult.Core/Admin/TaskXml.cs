using System.Security;

namespace Pult.Core.Admin;

public static class TaskXml
{
    public const string TaskName = "Pult Admin";
    /// Второе задание: тот же PultAdmin.exe с ФИКСИРОВАННЫМ аргументом, от SYSTEM. Запускает его только помощник пользователя (schtasks /Run).
    public const string SystemTaskName = "Pult System", SystemJobArgument = "--system-job";
    public const string SystemSid = "S-1-5-18";
    /// Кто может видеть/запускать/менять задание: только SYSTEM и Администраторы (Protected — без наследования).
    public const string SystemTaskSddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)";

    public static string Build(string userId, string exePath) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Pult: помощник администратора (драйверы, точка восстановления, очистка)</Description></RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <ExecutionTimeLimit>PT95M</ExecutionTimeLimit>
            <Hidden>true</Hidden>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author"><Exec><Command>"{SecurityElement.Escape(exePath)}"</Command></Exec></Actions>
        </Task>
        """;

    public static string BuildSystem(string exePath) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Pult: SYSTEM-исполнитель обслуживания (драйверы, точка восстановления, очистка). Запускается только помощником администратора.</Description>
            <SecurityDescriptor>{SystemTaskSddl}</SecurityDescriptor>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{SystemSid}</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <ExecutionTimeLimit>PT90M</ExecutionTimeLimit>
            <Hidden>true</Hidden>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author"><Exec><Command>"{SecurityElement.Escape(exePath)}"</Command><Arguments>{SystemJobArgument}</Arguments></Exec></Actions>
        </Task>
        """;
}
