namespace FaultSweep.Reporting;

public interface IRegressionTestGenerator
{
    string Generate(RegressionTestCase testCase);
}
