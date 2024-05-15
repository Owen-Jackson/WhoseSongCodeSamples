using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GOJ.Extensions
{
    public static class VisualElementExtensions
    {
        public static void Hide(this VisualElement element, bool affectLayout = true)
        {
            if (affectLayout)
                element.style.display = DisplayStyle.None;
            else
                element.style.visibility = Visibility.Hidden;
        }

        public static void Show(this VisualElement element)
        {
            element.style.display = DisplayStyle.Flex;
            element.style.visibility = Visibility.Visible;
        }

        public static void SetVisibility(this VisualElement element, bool visible, bool affectLayout = true)
        {
            if (visible)
                Show(element);
            else
                Hide(element, affectLayout);
        }
    }
}