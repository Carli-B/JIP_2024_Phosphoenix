using UnityEngine;
using UnityEngine.SceneManagement;

public class UserInterface : MonoBehaviour
{
    public string startSceneName = "StartScene"; 
    public string GlaucomaSceneName = "GlaucomaScene";
    public string MacularDegenerationSceneName = "MacularDegenerationScene";
    public string CompleteBlindnessSceneName = "CompleteBlindnessScene";
    void Update()
    {

        if (Input.GetKeyDown(KeyCode.Q))
        {
            // Load the Start Scene
            SceneManager.LoadScene(startSceneName);
        }

        if (Input.GetKeyDown(KeyCode.G))
        {
            // Load the Start Scene
            SceneManager.LoadScene(GlaucomaSceneName);
        }

        if (Input.GetKeyDown(KeyCode.M))
        {
            // Load the Start Scene
            SceneManager.LoadScene(MacularDegenerationSceneName);
        }

        if (Input.GetKeyDown(KeyCode.B))
        {
            // Load the Start Scene
            SceneManager.LoadScene(CompleteBlindnessSceneName);
        }
    }
}
