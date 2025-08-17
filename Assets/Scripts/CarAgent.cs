using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class CarAgent : Agent
{
    public float moveSpeed = 5f;
    public float turnSpeed = 100f;
    private Rigidbody rb;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
    }

    public override void OnEpisodeBegin()
    {
        // Reset agent position and velocity
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.localPosition = new Vector3(0, 0.5f, 0);
        transform.rotation = Quaternion.identity;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // Observe agent's local position and velocity
        sensor.AddObservation(transform.localPosition);
        sensor.AddObservation(rb.velocity);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int steerAction = actions.DiscreteActions[0]; // 0=Left, 1=Straight, 2=Right
        int throttleAction = actions.DiscreteActions[1]; // 0=Idle, 1=Accelerate, 2=Brake, 3=Reverse

        float move = 0f;
        float turn = 0f;
        
        // Throttle logic
        switch (throttleAction)
        {
            case 1: move = 1f; break; // Accelerate
            case 2: move = -0.5f; break; // Brake
            case 3: move = -1f; break; // Reverse
            default: move = 0f; break; // Idle
        }
        
        // Steering logic
        switch (steerAction)
        {
            case 0: turn = -1f; break; // Left
            case 2: turn = 1f; break; // Right
            default: turn = 0f; break; // Straight
        }

        // Apply motion
        Vector3 movement = transform.forward * move * moveSpeed * Time.deltaTime;
        rb.MovePosition(rb.position + movement);

        Quaternion rotation = Quaternion.Euler(0f, turn * turnSpeed * Time.deltaTime, 0f);
        rb.MoveRotation(rb.rotation * rotation);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discreteActions = actionsOut.DiscreteActions;
        
        // Steering
        if (Input.GetKey(KeyCode.LeftArrow))
            discreteActions[0] = 0; // Left
        else if (Input.GetKey(KeyCode.RightArrow))
            discreteActions[0] = 2; // Right
        else
            discreteActions[0] = 1; // Straight
        
        // Throttle
        if (Input.GetKey(KeyCode.UpArrow))
            discreteActions[1] = 1; // Accelerate
        else if (Input.GetKey(KeyCode.DownArrow))
            discreteActions[1] = 3; // Reverse
        else
            discreteActions[1] = 0; // Idle
    }
}