using System.Collections;
using System.Collections.Generic;
using UnityEngine;



public class SPH : MonoBehaviour
{
    
    [System.Serializable]
    public class Particle {
        public float lambda;
        public float density;

        public Vector3 currentForce; // f 貌似没有地方添加了这个force

        public Vector3 velocity; // v
        public Vector3 position; // x
        public Vector3 deltaP;
        public Vector3 predictPosition; // x*

        public List<Particle> neighbors = new List<Particle>();

        public bool onBoundary = false;
    }

    [Header("Display")]
    public bool wireframeSpheres = false;

    // Water position & boundary size & other constants
    [Header("General")]
    public bool showSpheres = false;
    public Vector3Int numToSpawn = new Vector3Int(10, 10, 10);
    public Vector3 boxSize = new Vector3(4, 10, 3);
    public Vector3 spawnBoxCenter = new Vector3(0, 3, 0);
    public Vector3 spawnBox = new Vector3(4, 2, 1.5f);
    public float particleRadius = 0.1f; // h
    public Vector3 gravity = new Vector3(0, -9.81f, 0);

    [Header("Fluid Constants")]
    public float boundDamping = -0.5f;
    public float viscosity = 200f;
    public float particleMass = 2.5f; // m
    public float gasConstant = 2000.0f; // Includes temp
    public float restingDensity = 300.0f; // ro 0
    public float epsilon = 0;
    public float k = 0.1f;
    public int n = 4;
    public float deltaQCoefficient = 0.1f; // |delta q| = deltaQCoefficient * h

    [Header("Particle Rendering")]
    public Mesh particleMesh;
    public float particleRenderSize = 16f;
    public Material material;

    [Header("Time")]
    public float timestep = 0.0001f;
    public Transform sphere;

    [Header("Compute")]
    public ComputeShader shader;
    public Particle[] particles;

    private ComputeBuffer _argsBuffer;
    public ComputeBuffer _particlesBuffer;

    private int num = 0;

    private static readonly int SizeProperty = Shader.PropertyToID("_size");
    private static readonly int ParticlesBufferProperty = Shader.PropertyToID("_particlesBuffer");
    private int densityPressureKernel;
    private int computeForceKernel;
    private int integrateKernel;

    private void Awake()
    {
        // Spawn Particles
        SpawnParticlesInBox();

        uint[] args = {
            particleMesh.GetIndexCount(0),
            (uint) num,
            particleMesh.GetIndexStart(0),
            particleMesh.GetBaseVertex(0),
            0
        };
        _argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        _argsBuffer.SetData(args);
        InitializeComputeBuffers();
    }

    private void InitializeComputeBuffers()
    {
        _particlesBuffer = new ComputeBuffer(num, 44);
        _particlesBuffer.SetData(particles);

        densityPressureKernel = shader.FindKernel("ComputeDensityPressure");
        computeForceKernel = shader.FindKernel("ComputeForces");
        integrateKernel = shader.FindKernel("Integrate");

        shader.SetInt("particleLength", num);
        shader.SetFloat("particleMass", particleMass);
        shader.SetFloat("viscosity", viscosity);
        shader.SetFloat("gasConstant", gasConstant);
        shader.SetFloat("restDensity", restingDensity);
        shader.SetFloat("boundDamping", boundDamping);

        shader.SetFloat("radius", particleRadius);
        shader.SetFloat("radius2", particleRadius * particleRadius);
        shader.SetFloat("radius3", particleRadius * particleRadius * particleRadius);
        shader.SetFloat("radius4", particleRadius * particleRadius * particleRadius * particleRadius);
        shader.SetFloat("radius5", particleRadius * particleRadius * particleRadius * particleRadius * particleRadius);

        shader.SetFloat("pi", Mathf.PI);
        shader.SetFloat("densityWeightConstant", 0.00497359197162172924277761760539f);
        shader.SetFloat("spikyGradient", -0.09947183943243458485555235210782f);
        shader.SetFloat("viscLaplacian", 0.39788735772973833942220940843129f);


        shader.SetVector("boxSize", boxSize);

        shader.SetBuffer(densityPressureKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(computeForceKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(integrateKernel, "_particles", _particlesBuffer);

    }

    private void OnDrawGizmos() {
        Gizmos.color = Color.blue;
        Gizmos.DrawWireCube(Vector3.zero, boxSize);

        if (!Application.isPlaying)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(spawnBoxCenter, spawnBox);
        }
        else {
            // Show Particles
            foreach (Particle p in particles) {

                // if (!p.onBoundary)
                // Gizmos.color = Color.white;
                // else
                // Gizmos.color = Color.red;

                Gizmos.color = Color.cyan;

                if (!wireframeSpheres)
                    Gizmos.DrawSphere(p.position, particleRadius);
                else
                    Gizmos.DrawWireSphere(p.position, particleRadius);
            }
        }
    }

    // 初始化particles
    private void SpawnParticlesInBox() {
        Vector3 spawnTopLeft = spawnBoxCenter - spawnBox / 2;
        List<Particle> _particles = new List<Particle>();

        for (int x = 0; x < numToSpawn.x; x++)
        {
            for (int y = 0; y < numToSpawn.y; y++)
            {
                for (int z = 0; z < numToSpawn.z; z++)
                {
                    Vector3 spawnPosition = spawnTopLeft + new Vector3(x * particleRadius * 2, y * particleRadius * 2, z * particleRadius * 2) + Random.onUnitSphere * particleRadius * 0.1f;
                    Particle p = new Particle
                    {
                        position = spawnPosition
                    };

                    _particles.Add(p);
                }
            }
        }

        num = _particles.Count;
        particles = _particles.ToArray();
    }

    // Run after accelerations have been set
    // 在计算之后移动particles
    private void MoveParticles (float timestep) {
        
        Vector3 topRight = boxSize / 2;
        Vector3 bottomLeft = -boxSize / 2;
        
        for (int i = 0; i < particles.Length; i++)
        {
            Particle p = particles[i];

            p.velocity += timestep * p.currentForce / particleMass;
            p.position += timestep * p.velocity;

            p.onBoundary = false;

            // Minimum Enforcements

            if (p.position.x - particleRadius < bottomLeft.x) {
                p.velocity.x *= boundDamping;
                p.position.x = bottomLeft.x + particleRadius;
                p.onBoundary = true;
            }

            if (p.position.y - particleRadius < bottomLeft.y) {
                p.velocity.y *= boundDamping;
                p.position.y = bottomLeft.y + particleRadius;
                p.onBoundary = true;
            }

            if (p.position.z - particleRadius < bottomLeft.z) {
                p.velocity.z *= boundDamping;
                p.position.z = bottomLeft.z + particleRadius;
                p.onBoundary = true;
            }

            // Maximum Enforcements

            if (p.position.x + particleRadius > topRight.x) {
                p.velocity.x *= boundDamping;
                p.position.x = topRight.x - particleRadius;
                p.onBoundary = true;
            }

            if (p.position.y + particleRadius > topRight.y) {
                p.velocity.y *= boundDamping;
                p.position.y = topRight.y - particleRadius;
                p.onBoundary = true;
            }

            if (p.position.z + particleRadius > topRight.z) {
                p.velocity.z *= boundDamping;
                p.position.z = topRight.z - particleRadius;
                p.onBoundary = true;
            }
        }
    }
    
    [Header("Error Reduction")]
    public float scalingConstant = 0.004f;

    private void ApplyForces() {
        // Line 1-4
    }

    private void FindNeighbors() { 
        // Line 5-7
    }

    // 计算标准SPH W(pi-pj, h) input： 两个particle距离
    // 公式参考yang's blog （12）
    private float CalculateW(float r) {
        return 0;
    }

    // 计算 W 的 gradient
    // 公式参考yang's blog （16）
    private Vector3 CalculateGradientW(float r, Vector3 direction)
    {
        return new Vector3();
    }

    // 计算ro i，update，计算Ci（lambda）需要用到
    // 公式参考 Muller（2）
    private void CalculateDensity()
    {

    }

    // 计算lambda，update
    // 公式参考 Muller（11）
    private void CalculateLambda()
    {
        // Line 9-11
    }

    // calculate & update deltaP & collision detection?
    // 公式参考 Muller（14）
    // 也需要计算Scorr
    private void CalculateDeltaP()
    {
        // Line 12-15
    }

    private void UpdatePredictPosition() 
    { 
        // Line 16-18
    }

    // update velocity & vorticity? & update x* to x
    private void UpdateFinalPosition()
    {
        // Line 20-24
    }

    private void Update()
    {
        if (showSpheres) Graphics.DrawMeshInstancedIndirect(particleMesh, 0, material, new Bounds(Vector3.zero, boxSize), _argsBuffer, castShadows: UnityEngine.Rendering.ShadowCastingMode.Off);
    }

    private void FixedUpdate() 
    {

        ApplyForces();

        FindNeighbors();

        CalculateLambda();

        CalculateDeltaP();

        UpdatePredictPosition();

        UpdateFinalPosition();

        MoveParticles(timestep);

        shader.SetVector("boxSize", boxSize);
        shader.SetFloat("timestep", timestep);
        shader.SetVector("spherePos", sphere.transform.position);
        shader.SetFloat("sphereRadius", sphere.transform.localScale.x / 2);

        shader.Dispatch(densityPressureKernel, num / 100, 1, 1);
        shader.Dispatch(computeForceKernel, num / 100, 1, 1);
        shader.Dispatch(integrateKernel, num / 100, 1, 1);

        material.SetFloat(SizeProperty, particleRenderSize);
        material.SetBuffer(ParticlesBufferProperty, _particlesBuffer);
    }

}


