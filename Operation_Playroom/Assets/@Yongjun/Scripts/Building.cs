using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class Building : NetworkBehaviour
{
    // 건물 체력
    public NetworkVariable<int> health = new NetworkVariable<int>(100, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 스크립터블 오브젝트
    [SerializeField] BuildingScriptableObject buildingData;
    [SerializeField] SoundScriptableObject soundData;

    // 중복 철거 방지
    public bool isDestruction = false;

    // 건물 메쉬
    MeshFilter meshFilter;

    // 오디오 소스
    [SerializeField] AudioSource audioSource;

    // 건물 체력 상태
    int currentState = 3; // 3: 기본, 2: 손상, 1: 많이 손상

    // 건물 팀
    public Owner buildingOwner;

    NoiseCheckManager noise;

    // 소음 매니저는 처음 필요할 때 한 번만 찾아 캐싱
    NoiseCheckManager Noise => noise != null ? noise : (noise = FindFirstObjectByType<NoiseCheckManager>());

    void Start()
    {
        meshFilter = GetComponent<MeshFilter>();
        audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
    }

    void Update()
    {
        buildingOwner = GetComponentInParent<OccupySystem>().currentOwner;
    }

    public override void OnNetworkSpawn()
    {
        health.OnValueChanged -= OnHealthChange;
        health.OnValueChanged += OnHealthChange;
    }

    public override void OnNetworkDespawn()
    {
        health.OnValueChanged -= OnHealthChange;
    }

    public void OnHealthChange(int oldVal, int newVal)
    {
        int damage = oldVal - newVal;
        if (damage > 0)
        {
            if (IsServer)
            {
                StartCoroutine(PlayDamageEffect());
                PlaySFXClientRpc(Random.Range(4, 9));
            }
        }
        UpdateBuildingMesh(newVal);

        if (newVal <= 0 && !isDestruction)
        {
            isDestruction = true;
            if (IsServer)
            {
                DestructionBuilding();
            }
        }
    }

    public void BuildingInit()
    {
        health.Value = buildingData.health;

        StartCoroutine(RaiseBuilding(2f));
    }

    IEnumerator RaiseBuilding(float duration)
    {
        float elapsedTime = 0f;
        Vector3 startPos = new Vector3(0, -25f, 0);
        Vector3 targetPos = new Vector3(0f, 5f, 0f);

        Noise.SubmitNoiseTo(2);
        GameObject buildEffect = Managers.Resource.Instantiate("BuildingSmokeEffect", null, true);
        SetActiveOnClients(buildEffect, true);
        if (buildEffect.GetComponent<NetworkObject>().TrySetParent(transform.parent, true))
        {
            buildEffect.transform.localPosition = Vector3.zero;
        }

        PlaySFXClientRpc(Random.Range(0, 3));

        while (elapsedTime < duration)
        {
            transform.localPosition = Vector3.Lerp(startPos, targetPos, elapsedTime / duration);
            elapsedTime += Time.deltaTime;
            yield return null;
        }
        transform.localPosition = targetPos;

        Managers.Pool.Push(buildEffect);
        SetActiveOnClients(buildEffect, false);

        GameObject sparkleEffect = Managers.Resource.Instantiate("BuildCompleteEffect");
        SetActiveOnClients(sparkleEffect, true);
        if (sparkleEffect.GetComponent<NetworkObject>().TrySetParent(transform, true))
        {
            sparkleEffect.transform.localPosition = Vector3.zero;
            yield return new WaitForSeconds(0.5f);
            Managers.Pool.Push(sparkleEffect);
            SetActiveOnClients(sparkleEffect, false);
        }
        isDestruction = false;
    }

    public void DestructionBuilding()
    {
        GetComponentInParent<OccupySystem>().ResetOwnership();
        GameObject destructionEffect = Managers.Resource.Instantiate("BuildingDestroyEffect", null, true);
        Noise.SubmitNoiseTo(2);
        SetActiveOnClients(destructionEffect, true);
        if (destructionEffect.GetComponent<NetworkObject>().TrySetParent(transform.parent, true))
        {
            destructionEffect.transform.localPosition = Vector3.zero;
            StartCoroutine(DelayPushEffect(destructionEffect, 3.25f));
            transform.localPosition = new Vector3(0, -25f, 0);
        }

        PlaySFXClientRpc(3);
    }

    IEnumerator DelayPushEffect(GameObject effect, float time)
    {
        yield return new WaitForSeconds(time);
        Managers.Pool.Push(effect);
        SetActiveOnClients(effect, false);
        Managers.Pool.Push(gameObject);
        SetActiveOnClients(gameObject, false);
    }

    // 서버에서 만든 오브젝트의 활성 상태를 모든 클라이언트에 맞춘다
    void SetActiveOnClients(GameObject target, bool isActive)
    {
        ActiveNetworkObjectClientRpc(target.GetComponent<NetworkObject>().NetworkObjectId, isActive);
    }

    [ClientRpc]
    void ActiveNetworkObjectClientRpc(ulong networkObjectId, bool isActive)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out NetworkObject networkObject))
        {
            networkObject.gameObject.SetActive(isActive);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void TakeDamageServerRpc(int damage, ulong clientId)
    {
        TakeDamage(damage, clientId);
    }

    public void TakeDamage(int damage, ulong clientId)
    {
        if (health.Value > 0)
        {
            if(health.Value - damage <= 0 && !isDestruction)
            {
                PlayData destroyPlayerData = GameManager.Instance.userPlayDatas[clientId];
                destroyPlayerData.destroy++;
                GameManager.Instance.userPlayDatas[clientId] = destroyPlayerData;
            }
            health.Value -= damage;
        }
    }

    void UpdateBuildingMesh(int health)
    {
        float healthPer = (float)health / buildingData.health;
        int state = (healthPer > 0.6f) ? 3 : (healthPer > 0.2f) ? 2 : 1;

        if (currentState == state) return;

        currentState = state;

        meshFilter.mesh = state == 3 ? buildingData.defaultMesh
                        : state == 2 ? buildingData.brokenMesh
                        : buildingData.hardBrokenMesh;
    }

    [ClientRpc]
    void PlaySFXClientRpc(int index, float volume = 1)
    {
        Debug.Assert(audioSource != null, $"{gameObject}: AudioSource is null");
        Debug.Assert(index >= 0 && index < soundData.soundClips.Length, $"{gameObject}: AudioClip is invalid");

        AudioClip clip = soundData.soundClips[index];
        Debug.Assert(clip != null, $"{gameObject}: AudioClip is not assigned");
        audioSource.volume = volume;
        audioSource.PlayOneShot(clip);
    }

    IEnumerator PlayDamageEffect()
    {
        GameObject damageEffect = Managers.Resource.Instantiate("BuildingDamageEffect", null, true);
        SetActiveOnClients(damageEffect, true);
        if (damageEffect.GetComponent<NetworkObject>().TrySetParent(transform.parent, true))
        {
            damageEffect.transform.localPosition = Vector3.zero;
            yield return new WaitForSeconds(1f);
            Managers.Pool.Push(damageEffect);
            SetActiveOnClients(damageEffect, false);
        }
    }
}