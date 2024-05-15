using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GOJ.UI.CustomElements
{
    public class PlayerToggle : BaseField<bool>
    {
        public new class UxmlFactory : UxmlFactory<PlayerToggle, UxmlTraits> { }
        public new class UxmlTraits : BaseFieldTraits<bool, UxmlBoolAttributeDescription> 
        {
            UxmlStringAttributeDescription m_playerName = new UxmlStringAttributeDescription { name = "player-name", defaultValue = null };
            UxmlColorAttributeDescription m_colour = new UxmlColorAttributeDescription { name = "player-colour", defaultValue = Color.white };
            UxmlIntAttributeDescription m_score = new UxmlIntAttributeDescription { name = "player-score", defaultValue = 0 };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);

                var toggle = ve as PlayerToggle;
                toggle.PlayerName = m_playerName.GetValueFromBag(bag, cc);
                toggle.PlayerColour = m_colour.GetValueFromBag(bag, cc);
                toggle.PlayerScore = m_score.GetValueFromBag(bag, cc);
            }
        }

        static readonly new string ussClassName = "player-toggle";
        static readonly new string inputUssClassName = "player-toggle__input";
        static readonly string inputCheckedUssClassName = "player-toggle__input--checked";
        static readonly new string labelUssClassName = "player-toggle__label";
        static readonly string playerScoreUssClassName = "player-toggle__score-label";
        static readonly string answerUssClassName = "player-toggle--is-answer";

        string m_playerName;
        public string PlayerName
        {
            get
            {
                return m_playerName;
            }
            set
            {
                m_playerName = value;
                label = value;
            }
        }

        Color m_playerColour;
        public Color PlayerColour
        {
            get
            {
                return m_playerColour;
            }
            set
            {
                m_playerColour = value;
                UpdatePlayerColour();
            }
        }

        int m_previousPlayerScore = 0;
        int m_playerScore;
        public int PlayerScore
        {
            get { return m_playerScore; }
            set
            {
                m_previousPlayerScore = m_playerScore;
                m_playerScore = value;
                UpdateScoreLabel(value);
            }
        }

        bool m_isConnected = true;

        VisualElement m_input;
        Label m_playerScoreLabel;

        public PlayerToggle() : this(null, Color.white, 0) { }

        public PlayerToggle(string playerName, Color playerColour, int startingScore) : base(playerName, null)
        {
            m_playerName ??= playerName;
            m_playerColour = playerColour;

            AddToClassList(ussClassName);                       

            m_input = this.Q(className: BaseField<bool>.inputUssClassName);
            m_input.AddToClassList(inputUssClassName);

            labelElement.ClearClassList();
            labelElement.AddToClassList(labelUssClassName);

            m_playerScoreLabel = Helpers.UIElementsHelpers.CreateVisualElement<Label>("PlayerScoreLabel");
            m_playerScoreLabel.AddToClassList(playerScoreUssClassName);
            Add(m_playerScoreLabel);            
            PlayerScore = startingScore;
            
            SetOutline();

            RegisterCallback<ClickEvent>(OnClick);
            RegisterCallback<NavigationSubmitEvent>(OnSubmit);
        }

        void OnClick(ClickEvent evt)
        {
            TryToggle(evt);
        }

        void OnSubmit(NavigationSubmitEvent evt)
        {
            TryToggle(evt);
        }

        void TryToggle(EventBase evt)
        {
            if (pickingMode == PickingMode.Ignore)
            {
                evt.StopPropagation();
                return;
            }

            var playerToggle = evt.currentTarget as PlayerToggle;
            playerToggle.ToggleValue();

            evt.StopPropagation();
        }

        void ToggleValue()
        {
            value = !value;
        }

        public override void SetValueWithoutNotify(bool newValue)
        {
            m_input.EnableInClassList(inputCheckedUssClassName, newValue);

            base.SetValueWithoutNotify(newValue);
            UpdatePlayerColour();
        }

        void UpdatePlayerColour()
        {
            if(value)            
                SetToCheckedColour();            
            else            
                SetToUncheckedColour();            

            SetOutline();
        }

        public void SetIsConnectedAndUpdateColours(bool isConnected)
        {
            m_isConnected = isConnected;
            UpdatePlayerColour();
        }

        void SetToCheckedColour()
        {
            labelElement.style.color = Color.white;

            style.backgroundColor = m_isConnected ? m_playerColour : Color.gray;
        }

        void SetToUncheckedColour()
        {
            labelElement.style.color = m_isConnected ? Color.white : Color.gray;

            style.backgroundColor = Color.clear;
        }

        void SetOutline()
        {
            Color outlineColour = m_isConnected ? m_playerColour : Color.gray;

            style.borderTopColor = outlineColour;
            style.borderBottomColor = outlineColour;
            style.borderLeftColor = outlineColour;
            style.borderRightColor = outlineColour;
        }

        public void SetAsAnswer()
        {
            AddToClassList(answerUssClassName);
            GameManager.Instance.StartCoroutine(RainbowColourEffect());
        }

        public void UpdatePlayerNameWithScore(bool isCorrect)
        {
            label = string.Format("{0} {1}", m_playerName, isCorrect ? "+3" : "-1");
        }

        void UpdateScoreLabel(int newScore)
        {
            if (Application.isPlaying)
            {
                GameManager.Instance.StartCoroutine(AnimateScoreChange());
            }
            else
            {
                m_playerScoreLabel.text = newScore.ToString();
            }
        }

        public void RemoveTransitions()
        {
            label = m_playerName;

            if(ClassListContains(answerUssClassName))
                RemoveFromClassList(answerUssClassName);            
        }

        IEnumerator RainbowColourEffect()
        {
            Color.RGBToHSV(m_playerColour, out float hue, out float saturation, out float value);

            while(ClassListContains(answerUssClassName))
            {
                hue += Time.deltaTime;

                if(hue > 1)                
                    hue -= 1;                

                StyleColor nextColour = new StyleColor(Color.HSVToRGB(hue, 1, 1));

                style.borderRightColor = nextColour;
                style.borderLeftColor = nextColour;
                style.borderTopColor = nextColour;
                style.borderBottomColor = nextColour;

                yield return null;
            }

            SetToUncheckedColour();
        }

        IEnumerator AnimateScoreChange()
        {
            int scoreDelta = PlayerScore - m_previousPlayerScore;

            if (scoreDelta == 0)
            {
                m_playerScoreLabel.text = PlayerScore.ToString();
                yield break;
            }

            float duration = 1f;
            float t = 0f;
            int startingScore = m_previousPlayerScore;

            while(t < 1f)
            {
                t += Time.deltaTime / duration;

                float value = Mathf.SmoothStep(startingScore, PlayerScore, t);
                int currentscore = Mathf.RoundToInt(value);

                m_playerScoreLabel.text = currentscore.ToString();

                yield return null;
            }
        }
    }
}