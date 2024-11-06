using UnityEngine;
using UnityEngine.SceneManagement;

public class ExitScene : MonoBehaviour
{
    public string startSceneName = "StartScene"; 
    void Update()
    {

        if (Input.GetKeyDown(KeyCode.Q))
        {
            // Load the Start Scene
            SceneManager.LoadScene(startSceneName);
        }
    }
}